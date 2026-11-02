using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeepDive.Core.Launcher;

namespace DeepDive.Core.Modrinth;

/// <summary>整合包安装进度。</summary>
/// <param name="Stage">当前在干什么（「下载模组 12/48」）。</param>
/// <param name="Fraction">0~1；小于 0 表示这一步没有可量化的进度。</param>
public sealed record ModpackProgress(string Stage, double Fraction)
{
    public bool IsDeterminate => Fraction >= 0;
}

/// <summary>
/// 「懒人包」缺关键信息，装不下去了 —— 界面收到这个要<b>问用户补齐再重试</b>。
/// </summary>
/// <remarks>
/// <para>
/// 目前只有一种情况：<b>猜不出该用哪个 Minecraft 版本</b>。懒人包里没清单文件，
/// 版本信息只能从 jar 里反推；碰上「一个模组都没有」的包（纯配置+存档），
/// 或者模组都是没写版本声明的老东西，就反推不出来。
/// </para>
/// <para>
/// <b>故意用异常而不是返回值</b>：这条路上不能有「装了一半的实例」，
/// 抛出时实例还没建（见 <see cref="ModpackInstaller"/> 里的调用顺序），
/// 用户补完版本重试时环境是干净的。
/// </para>
/// </remarks>
public sealed class ModpackNeedsInfoException : Exception
{
    public ModpackNeedsInfoException(string message, ModLoaderKind detectedLoader, int jarCount)
        : base(message)
    {
        DetectedLoader = detectedLoader;
        JarCount = jarCount;
    }

    /// <summary>已经看出来的加载器（可能仍是 <see cref="ModLoaderKind.Vanilla"/>，即没看出来）。</summary>
    public ModLoaderKind DetectedLoader { get; }

    /// <summary>扫了多少个 jar。</summary>
    public int JarCount { get; }

    /// <summary>缺的是什么。目前只有「游戏版本」这一种。</summary>
    public string Missing => "Minecraft 版本";
}

/// <summary>整合包安装结果。</summary>
/// <param name="InstanceName">新建的实例名。</param>
/// <param name="InstanceDirectory">实例目录。</param>
/// <param name="VersionName">版本目录名（启动时用这个名字）。</param>
/// <param name="MinecraftVersion">整合包要求原版版本。</param>
/// <param name="LoaderLabel">加载器（人话）。</param>
/// <param name="Files">下载了多少个文件。</param>
/// <param name="Warnings">没做成的事。</param>
public sealed record ModpackInstallResult(
    string InstanceName,
    string InstanceDirectory,
    string VersionName,
    string MinecraftVersion,
    string LoaderLabel,
    int Files,
    IReadOnlyList<string> Warnings)
{
    public string Summary => Files == 0
        ? $"实例「{InstanceName}」建好了，版本 {VersionName}（{MinecraftVersion} · {LoaderLabel}）"
        : $"实例「{InstanceName}」装好了：{VersionName}（{MinecraftVersion} · {LoaderLabel}），{Files} 个文件";
}

/// <summary>
/// 把 Modrinth 的整合包（<c>.mrpack</c>）装成一个新实例。
/// </summary>
/// <remarks>
/// <para>
/// <b>这跟「把 jar 丢进 mods」是两件完全不同的事</b>，所以单独一个类：
/// </para>
/// <list type="number">
///   <item>下载 <c>.mrpack</c>（本质是个 zip）；</item>
///   <item>读里面的 <c>modrinth.index.json</c> —— 它声明了要哪个游戏版本、哪个加载器、
///         以及几十上百个文件的下载地址和 sha1；</item>
///   <item>新建一个独立实例（不能污染现有实例）；</item>
///   <item>先把原版 + 加载器装好（整合包自己不带这些）；</item>
///   <item>按 index 把文件下到实例目录里（路径是相对的，比如 <c>mods/xxx.jar</c>）；</item>
///   <item>把 <c>overrides/</c> 里的配置解压覆盖上去。</item>
/// </list>
/// <para>
/// <b>顺序不能换</b>：加载器必须先装，否则 <c>versions/</c> 里没有版本目录，
/// 后面往 <c>mods/</c> 下文件时连父目录都不存在；而 overrides 必须最后解压，
/// 因为它的作用就是覆盖前面下载的默认配置。
/// </para>
/// </remarks>
public sealed class ModpackInstaller : IDisposable
{
    /// <summary>同时下几个文件。串行下 40 个模组要一分钟，4 个并发能压到十几秒。</summary>
    private const int MaxParallelDownloads = 4;

    private readonly InstanceManager _instances;
    private readonly LoaderInstallerService _loaders;
    private readonly MinecraftLauncherService _core;
    private readonly HttpClient _http;

    public ModpackInstaller(
        InstanceManager instances,
        LoaderInstallerService loaders,
        MinecraftLauncherService core)
    {
        _instances = instances;
        _loaders = loaders;
        _core = core;

        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepDive/0.1.0 (Minecraft launcher; local build)");
    }

    /// <summary>日志回调（复用启动器的日志区）。</summary>
    public event Action<object, string>? Log;

    /// <summary>
    /// 安装一个整合包到新实例。
    /// </summary>
    /// <param name="version">整合包的文件版本（必须带 <c>.mrpack</c> 主文件）。</param>
    /// <param name="instanceName">新实例名。重名会抛异常，调用方负责先起个好名字。</param>
    /// <param name="progress">进度回调，<b>可能在后台线程调用</b>。</param>
    public async Task<ModpackInstallResult> InstallAsync(
        ModrinthVersion version,
        string instanceName,
        IProgress<ModpackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var packFile = version.Files.FirstOrDefault(f => f.FileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
                       ?? version.Files.FirstOrDefault(f => f.Primary)
                       ?? version.Files.FirstOrDefault();

        if (packFile is null)
            throw new InvalidOperationException("这个版本没有可下载的整合包文件。");

        // ── 先把 .mrpack 拿到本地 ──
        // 下划线开头：InstanceManager 扫实例时会跳过它，
        // 否则实例下拉里会多出一个叫「modpack-cache」的假实例
        var cacheDir = Path.Combine(_instances.InstancesRoot, "_modpack-cache");
        Directory.CreateDirectory(cacheDir);

        var packPath = Path.Combine(cacheDir, Sanitize(packFile.FileName));

        if (!File.Exists(packPath) || new FileInfo(packPath).Length != packFile.Size)
        {
            Log?.Invoke(this, $"下载整合包 {packFile.FileName}（{packFile.Size / 1024 / 1024d:F1} MB）…");

            await DownloadAsync(
                packFile.Url,
                packPath,
                packFile.Hashes.Sha1,
                "下载整合包",
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        return await InstallFromPackAsync(packPath, instanceName, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 从一个<b>本地</b> <c>.mrpack</c> 文件安装到新实例（跳过下载那一步）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用来支持「别人给你一个整合包文件，你双击/拖进来装上」这个场景。
    /// 包本身已经在盘上了，剩下的流程跟在线装完全一样 —— 所以两条路共用这一份实现，
    /// 不复制粘贴第二套解包逻辑。
    /// </para>
    /// <para>
    /// 包文件不会被移动到缓存目录：用户给的文件留在原地，我们只读不搬，
    /// 装完他想删就自己删（也可能还要留着给别人）。
    /// </para>
    /// </remarks>
    /// <param name="packPath">本地 <c>.mrpack</c> 的完整路径。</param>
    /// <param name="instanceName">新实例名。</param>
    public async Task<ModpackInstallResult> InstallFromPackAsync(
        string packPath,
        string instanceName,
        IProgress<ModpackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        if (!File.Exists(packPath))
            throw new FileNotFoundException($"找不到整合包文件：{packPath}", packPath);

        progress?.Report(new ModpackProgress("读取整合包配置", -1));

        var index = ReadIndex(packPath);

        return await InstallCoreAsync(
                index,
                gameDir => ExtractOverrides(packPath, "overrides", gameDir),
                instanceName,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 按压缩包里的内容<b>自动识别格式</b>再安装。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 手上拿到的压缩包可能是三种东西，用户不该被要求先搞清楚是哪种：
    /// </para>
    /// <list type="bullet">
    ///   <item><c>modrinth.index.json</c> —— Modrinth 的 <c>.mrpack</c>（也可能只是被改了扩展名）；</item>
    ///   <item><c>manifest.json</c> —— CurseForge 导出的标准 zip；</item>
    ///   <item>都没有，但里面有 <c>mods/</c> <c>config/</c> 这类目录 —— <b>懒人包</b>，
    ///         版本和加载器从 jar 里反推（见 <see cref="LooseZipScanner"/>）。</item>
    /// </list>
    /// <para>
    /// <b>CurseForge 的坑</b>：它的 manifest 只写「项目 ID + 文件 ID」，不写文件名，
    /// 也不写下载地址。所以必须逐个反查才能知道下什么、往哪儿放。
    /// 这里走的是社区维护的 CurseForge 查询服务（<c>api.cfwidget.com</c>，按项目 ID 查文件名），
    /// 下载走 Forge CDN 直链，都不需要 API key；查不到的文件记进 warnings，不打断整包。
    /// </para>
    /// </remarks>
    /// <param name="minecraftVersionHint">
    /// 用户手动指定的游戏版本。只在懒人包反推不出来时才需要 ——
    /// 抛 <see cref="ModpackNeedsInfoException"/> 之后由界面问完再传进来。
    /// </param>
    public async Task<ModpackInstallResult> InstallFromArchiveAsync(
        string archivePath,
        string instanceName,
        IProgress<ModpackProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? minecraftVersionHint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        if (!File.Exists(archivePath))
            throw new FileNotFoundException($"找不到整合包文件：{archivePath}", archivePath);

        using (var probe = ZipFile.OpenRead(archivePath))
        {
            if (probe.GetEntry("modrinth.index.json") is not null)
                return await InstallFromPackAsync(archivePath, instanceName, progress, cancellationToken)
                    .ConfigureAwait(false);

            if (probe.GetEntry("manifest.json") is not null)
                return await InstallFromCurseForgeAsync(archivePath, instanceName, progress, cancellationToken)
                    .ConfigureAwait(false);

            var names = probe.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Select(e => e.FullName.Replace('\\', '/'))
                .ToList();

            if (LooseZipScanner.LooksLikeLoosePack(names))
                return await InstallFromLooseZipAsync(
                        archivePath, instanceName, minecraftVersionHint, progress, cancellationToken)
                    .ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            "这个压缩包里既没有 modrinth.index.json 也没有 manifest.json，"
            + "也找不到 mods / config / saves 这些目录，认不出是整合包。\n\n"
            + "确认一下是不是选错了文件（比如选成了服务端包、或者只是个说明文档的压缩包）。");
    }

    // ── 懒人包（无清单的 zip） ──────────────────────────

    /// <summary>
    /// 装「懒人包」：作者把整个游戏目录（<c>mods</c> / <c>config</c> / <c>saves</c>）直接压成 zip，
    /// 没有任何清单文件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 跟另外两种的根本区别：<b>没有清单就没有文件列表</b> —— 模组本体已经在压缩包里，
    /// 不需要下载。所以走的是「装好原版和加载器 → 把压缩包整个解开」这条路，
    /// <see cref="ModpackIndex.Files"/> 是空的。
    /// </para>
    /// <para>
    /// 版本信息全靠 <see cref="LooseZipScanner"/> 从 jar 里反推。反推不出来时抛
    /// <see cref="ModpackNeedsInfoException"/>，<b>不建实例</b> —— 让界面先问用户要版本，
    /// 问到了再来。这样不会留下一堆装了一半的空实例。
    /// </para>
    /// </remarks>
    private async Task<ModpackInstallResult> InstallFromLooseZipAsync(
        string zipPath,
        string instanceName,
        string? minecraftVersionHint,
        IProgress<ModpackProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ModpackProgress("扫描模组，识别版本与加载器", -1));

        string prefix;
        List<string> entryNames;

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            entryNames = archive.Entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Select(e => e.FullName.Replace('\\', '/'))
                .ToList();
        }

        prefix = LooseZipScanner.DetectContentPrefix(entryNames);

        var guess = LooseZipScanner.Scan(zipPath, prefix, message => Log?.Invoke(this, message));

        if (prefix.Length > 0)
            Log?.Invoke(this, $"压缩包里套了一层「{prefix}/」，解压时会剥掉");

        Log?.Invoke(this, $"懒人包识别：{guess.Evidence}");
        Log?.Invoke(this, $"判断结果：{LoaderLabel(guess.Kind, string.Empty)} · {guess.MinecraftVersion ?? "(版本未知)"}");

        var minecraftVersion = string.IsNullOrWhiteSpace(minecraftVersionHint)
            ? guess.MinecraftVersion
            : minecraftVersionHint.Trim();

        if (string.IsNullOrWhiteSpace(minecraftVersion))
        {
            throw new ModpackNeedsInfoException(
                guess.JarCount == 0
                    ? "这个压缩包里没有 mods 目录下的模组，猜不出该用哪个 Minecraft 版本。"
                    : $"翻了 {guess.JarCount} 个模组，没看出它们要哪个 Minecraft 版本。",
                guess.Kind,
                guess.JarCount);
        }

        var loaderKey = guess.Kind switch
        {
            ModLoaderKind.NeoForge => "neoforge",
            ModLoaderKind.Forge => "forge",
            ModLoaderKind.Fabric => "fabric-loader",
            _ => null,
        };

        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["minecraft"] = minecraftVersion,
        };

        // 空字符串 = 装这个游戏版本下最新的加载器。懒人包不写加载器版本，只能取最新；
        // 万一模组是给旧加载器写的，启动时会由崩溃分析指出方向（比装不上强）
        if (loaderKey is not null)
            dependencies[loaderKey] = string.Empty;

        var index = new ModpackIndex
        {
            FormatVersion = 1,
            Name = Path.GetFileNameWithoutExtension(zipPath),
            VersionId = string.Empty,
            Dependencies = dependencies,
            Files = new List<ModpackFile>(),
        };

        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(minecraftVersionHint))
            warnings.Add("这个包没写加载器版本，装的是该游戏版本下最新的加载器");

        var extracted = 0;

        var result = await InstallCoreAsync(
                index,
                gameDir => extracted = ExtractLooseZip(zipPath, prefix, gameDir, warnings),
                instanceName,
                progress,
                cancellationToken,
                warnings)
            .ConfigureAwait(false);

        return result with { Files = extracted };
    }

    /// <summary>把懒人包整个解开到游戏目录，剥掉 <paramref name="prefix"/> 那层。</summary>
    private static int ExtractLooseZip(string zipPath, string prefix, string gameDir, List<string> warnings)
    {
        var count = 0;
        var skipped = 0;

        using var archive = ZipFile.OpenRead(zipPath);

        var prefixWithSlash = prefix.Length == 0 ? string.Empty : prefix + "/";

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // 目录项

            var full = entry.FullName.Replace('\\', '/');

            if (IsArchiveJunk(full)) continue;

            if (prefixWithSlash.Length > 0)
            {
                if (!full.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase))
                {
                    // 前缀之外的东西（作者自己的 readme、截图）一律不往游戏目录里塞
                    skipped++;
                    continue;
                }

                full = full[prefixWithSlash.Length..];
            }

            // 白名单：只解已知的游戏目录和根目录游戏文件。
            // 压缩包里混着的说明、预览图、旧配置备份不该往用户游戏目录里塞
            if (!LooseZipScanner.IsContentPath(full))
            {
                skipped++;
                continue;
            }

            var target = SafeCombine(gameDir, full);
            if (target is null)
            {
                skipped++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            count++;
        }

        if (skipped > 0)
            warnings.Add($"压缩包里有 {skipped} 个文件不是游戏目录里的内容（说明、预览图之类），没解");

        return count;
    }

    /// <summary>打包工具留下的垃圾，解出来只会碍眼。</summary>
    private static bool IsArchiveJunk(string path)
        => path.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith("/.DS_Store", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("._", StringComparison.Ordinal)
           || path.EndsWith("/Thumbs.db", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith("/desktop.ini", StringComparison.OrdinalIgnoreCase);


    // ── CurseForge zip ──────────────────────────────────

    /// <summary>CurseForge 标准整合包的安装流程。</summary>
    private async Task<ModpackInstallResult> InstallFromCurseForgeAsync(
        string zipPath,
        string instanceName,
        IProgress<ModpackProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ModpackProgress("读取整合包配置", -1));

        var manifest = ReadCurseForgeManifest(zipPath);

        var mcVersion = manifest.Minecraft.Version;
        if (string.IsNullOrWhiteSpace(mcVersion))
            throw new InvalidOperationException("整合包的 manifest.json 里没写 Minecraft 版本。");

        var (kind, loaderVersion) = PickCurseForgeLoader(manifest.Minecraft.ModLoaders);

        Log?.Invoke(this,
            $"CurseForge 整合包「{manifest.Name}」{manifest.Version}："
            + $"{mcVersion} · {LoaderLabel(kind, loaderVersion)} · {manifest.Files.Count} 个模组");

        // ── 反查每个文件 ID 的真实文件名和下载地址 ──
        progress?.Report(new ModpackProgress("查询模组下载地址", -1));

        var warnings = new List<string>();
        var resolved = await ResolveCurseForgeFilesAsync(manifest.Files, progress, warnings, cancellationToken)
            .ConfigureAwait(false);

        if (resolved.Count == 0)
            throw new InvalidOperationException(
                $"这个整合包的 {manifest.Files.Count} 个模组一个都没查到文件信息，装下去也是空包。\n\n"
                + "前几条原因：\n"
                + string.Join("\n", warnings.Take(5)));

        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["minecraft"] = mcVersion,
        };

        var loaderKey = kind switch
        {
            ModLoaderKind.NeoForge => "neoforge",
            ModLoaderKind.Forge => "forge",
            ModLoaderKind.Fabric => "fabric-loader",
            _ => null,
        };

        if (loaderKey is not null && !string.IsNullOrWhiteSpace(loaderVersion))
            dependencies[loaderKey] = loaderVersion;

        var index = new ModpackIndex
        {
            FormatVersion = 1,
            Name = string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileNameWithoutExtension(zipPath) : manifest.Name,
            VersionId = manifest.Version,
            Dependencies = dependencies,
            Files = resolved,
        };

        // overrides 目录名由 manifest 指定，不是写死的 "overrides"
        var overridesPrefix = string.IsNullOrWhiteSpace(manifest.Overrides) ? "overrides" : manifest.Overrides;

        return await InstallCoreAsync(
                index,
                gameDir => ExtractOverrides(zipPath, overridesPrefix, gameDir),
                instanceName,
                progress,
                cancellationToken,
                warnings)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 把 manifest 里的「项目 ID + 文件 ID」逐条查成「文件名 + 下载地址 + 大小」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么绕这么一圈</b>：CurseForge 的 manifest 只给两个整数（项目 ID、文件 ID），
    /// 文件名和下载地址都没有。而 <c>mediafilez.forgecdn.net</c> 的直链格式是
    /// <c>files/{id/1000}/{id%1000}/{文件名}</c> —— <b>缺了文件名就拼不出地址</b>。
    /// </para>
    /// <para>
    /// 文件名从 <c>api.cfwidget.com/{项目ID}</c> 拿（社区维护的只读镜像，免费无限流）。
    /// CurseForge 官方 API（<c>api.curseforge.com</c>）要 key，官方网页版接口和
    /// <c>api.curse.tools</c> 在这台机器上都被 Cloudflare 挡成 403，实测只有 cfwidget 能过。
    /// </para>
    /// <para>
    /// 单条失败只记 warning，不打断整包 —— 少几个模组总比整个装不上强，
    /// 而且用户能从日志里看到具体缺哪几个。
    /// </para>
    /// </remarks>
    private async Task<List<ModpackFile>> ResolveCurseForgeFilesAsync(
        IReadOnlyList<CurseForgeFileRef> refs,
        IProgress<ModpackProgress>? progress,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var result = new List<ModpackFile>(refs.Count);

        // 同一个项目可能被引用多次（少见），查一次就够
        var projectCache = new Dictionary<int, Dictionary<int, CurseForgeProjectFile>?>();
        var cacheLock = new object();

        var done = 0;

        await Parallel.ForEachAsync(
            refs,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (fileRef, token) =>
            {
                try
                {
                    Dictionary<int, CurseForgeProjectFile>? projectFiles;

                    lock (cacheLock)
                    {
                        if (projectCache.TryGetValue(fileRef.ProjectId, out var cached))
                            projectFiles = cached;
                        else
                            projectFiles = null;
                    }

                    if (projectFiles is null)
                    {
                        projectFiles = await FetchProjectFilesAsync(fileRef.ProjectId, token).ConfigureAwait(false);

                        lock (cacheLock)
                            projectCache[fileRef.ProjectId] = projectFiles;
                    }

                    if (projectFiles is null)
                    {
                        lock (warnings) warnings.Add($"项目 {fileRef.ProjectId} 查不到文件列表（可能被作者删除或隐藏）");
                        return;
                    }

                    if (!projectFiles.TryGetValue(fileRef.FileId, out var file))
                    {
                        // 很常见：cfwidget 只给最近二三十个文件，整合包用的是老的
                        lock (warnings) warnings.Add(
                            $"项目 {fileRef.ProjectId} 的文件 {fileRef.FileId} 不在最近列表里，跳过");
                        return;
                    }

                    // Forge CDN 的直链规律：fileID 拆成「前段/后 3 位」。
                    // 文件名必须整体转义：CDN 把裸 + 当成空格处理，含 + 的名字（如
                    // sodium-neoforge-0.6.13+mc1.21.4.jar）不转义会直接 403。
                    // 实测：+ 保留 → 403，%2B → 200。
                    var escapedName = Uri.EscapeDataString(file.FileName);
                    var url = $"https://mediafilez.forgecdn.net/files/{fileRef.FileId / 1000}/{fileRef.FileId % 1000}/"
                              + escapedName;

                    lock (result)
                    {
                        result.Add(new ModpackFile
                        {
                            // CurseForge 的模组一律放 mods/，路径信息 manifest 里根本没有
                            Path = "mods/" + file.FileName,
                            Downloads = new List<string> { url },
                            Hashes = new ModrinthHashes(),   // cfwidget 不给哈希，靠大小判断
                            FileSize = file.FileSize,
                            Env = new ModpackEnv(),
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lock (warnings) warnings.Add($"项目 {fileRef.ProjectId}/{fileRef.FileId} 查询失败：{ex.Message}");
                }
                finally
                {
                    var current = Interlocked.Increment(ref done);
                    progress?.Report(new ModpackProgress(
                        $"查询模组下载地址 {current}/{refs.Count}",
                        refs.Count == 0 ? 1 : (double)current / refs.Count));
                }
            }).ConfigureAwait(false);

        if (result.Count < refs.Count)
        {
            var missing = refs.Count - result.Count;
            warnings.Add($"{missing} 个模组没查到文件信息（多半是 cfwidget 只保留最近文件），可以从 modlist.html 里手动补");
        }

        return result;
    }

    /// <summary>查一个项目的文件列表（fileID → 文件名 + 大小）。失败返回 null。</summary>
    private async Task<Dictionary<int, CurseForgeProjectFile>?> FetchProjectFilesAsync(
        int projectId, CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync($"https://api.cfwidget.com/{projectId}", cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return null;

        var map = new Dictionary<int, CurseForgeProjectFile>();

        foreach (var file in files.EnumerateArray())
        {
            if (!file.TryGetProperty("id", out var idNode)) continue;
            if (!file.TryGetProperty("name", out var nameNode)) continue;

            var name = nameNode.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var size = file.TryGetProperty("filesize", out var sizeNode) ? sizeNode.GetInt64() : 0;

            map[idNode.GetInt32()] = new CurseForgeProjectFile(name, size);
        }

        return map;
    }

    /// <summary>cfwidget 给的单个文件信息。</summary>
    private sealed record CurseForgeProjectFile(string FileName, long FileSize);

    /// <summary>读 CurseForge 的 <c>manifest.json</c>。</summary>
    private static CurseForgeManifest ReadCurseForgeManifest(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var entry = archive.GetEntry("manifest.json")
                    ?? throw new InvalidOperationException("这个 zip 里没有 manifest.json，不是 CurseForge 整合包。");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);

        return JsonSerializer.Deserialize<CurseForgeManifest>(reader.ReadToEnd(), new JsonSerializerOptions
               {
                   PropertyNameCaseInsensitive = true,
               })
               ?? throw new InvalidOperationException("manifest.json 解析失败。");
    }

    /// <summary>
    /// 从 <c>modLoaders[].id</c>（形如 <c>neoforge-21.4.135</c>）解析加载器。
    /// </summary>
    private static (ModLoaderKind Kind, string Version) PickCurseForgeLoader(
        IReadOnlyList<CurseForgeLoader> loaders)
    {
        // primary 的那条优先，没有就按顺序找第一个认识的
        var ordered = loaders.OrderByDescending(l => l.Primary).ToList();

        foreach (var loader in ordered)
        {
            var id = loader.Id ?? string.Empty;

            if (id.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
                return (ModLoaderKind.NeoForge, id["neoforge-".Length..]);

            if (id.StartsWith("forge-", StringComparison.OrdinalIgnoreCase))
                return (ModLoaderKind.Forge, id["forge-".Length..]);

            if (id.StartsWith("fabric-", StringComparison.OrdinalIgnoreCase))
                return (ModLoaderKind.Fabric, id["fabric-".Length..]);

            if (id.StartsWith("quilt-", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"这个整合包要 Quilt（{id}），本启动器还不支持 Quilt。");
        }

        return (ModLoaderKind.Vanilla, string.Empty);
    }

    private static string LoaderLabel(ModLoaderKind kind, string version) => kind switch
    {
        // 版本为空 = 装该游戏版本下的最新 —— 懒人包只能这么办，它不写加载器版本
        ModLoaderKind.Fabric => string.IsNullOrWhiteSpace(version) ? "Fabric（最新）" : $"Fabric {version}",
        ModLoaderKind.NeoForge => string.IsNullOrWhiteSpace(version) ? "NeoForge（最新）" : $"NeoForge {version}",
        ModLoaderKind.Forge => string.IsNullOrWhiteSpace(version) ? "Forge（最新）" : $"Forge {version}",
        _ => "原版",
    };

    /// <summary>
    /// 两条安装路径共用的核心：建实例 → 装原版和加载器 → 按清单下文件 → 解 overrides。
    /// </summary>
    /// <param name="index">统一的清单（Modrinth 直接给，CurseForge 由 manifest 转换而来）。</param>
    /// <param name="extractOverrides">把覆盖文件解到游戏目录，返回解了多少个。</param>
    /// <param name="warnings">已有的提醒（CurseForge 查地址时攒的），会继续往里加。</param>
    private async Task<ModpackInstallResult> InstallCoreAsync(
        ModpackIndex index,
        Func<string, int> extractOverrides,
        string instanceName,
        IProgress<ModpackProgress>? progress,
        CancellationToken cancellationToken,
        List<string>? warnings = null)
    {
        warnings ??= new List<string>();

        var minecraftVersion = index.Dependencies.TryGetValue("minecraft", out var mc) ? mc : string.Empty;
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new InvalidOperationException("整合包里没写要用哪个 Minecraft 版本（dependencies.minecraft 缺失）。");

        var (kind, loaderVersion) = PickLoader(index.Dependencies);
        var loaderLabel = LoaderLabel(kind, loaderVersion);

        Log?.Invoke(this,
            $"整合包「{index.Name}」：{minecraftVersion} · {loaderLabel} · {index.Files.Count} 个文件" +
            $" · 依赖最高 {index.FormatVersion} 版格式");

        // ── 建实例 ──
        progress?.Report(new ModpackProgress($"新建实例「{instanceName}」", -1));

        var instance = _instances.Create(instanceName);
        Log?.Invoke(this, $"已新建实例 {instance.Directory}");

        // 装加载器必须在这个实例里进行，否则会装到当前那个实例上
        _instances.SwitchTo(instance);

        try
        {
            // ── 原版 + 加载器 ──
            progress?.Report(new ModpackProgress($"安装 {minecraftVersion} · {loaderLabel}", -1));

            var installed = await _loaders
                .InstallAsync(kind, minecraftVersion, string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion,
                    cancellationToken)
                .ConfigureAwait(false);

            Log?.Invoke(this, $"加载器装好：{installed.VersionName}");

            // 往哪儿放文件：跟着「游戏实际的工作目录」走，不是实例根。
            // 版本隔离开启后 mods / config / resourcepacks 都在 versions/<版本名>/ 下面，
            // 游戏读的也是那里；写到实例根等于把整包模组丢进一个没人看的目录，
            // 启动后 Fabric 只认自带的那几个内置模组（实测「Loading 4 mods」）。
            var gameDir = _core.GetGameDir(installed.VersionName);
            Log?.Invoke(this, $"内容写入目录：{gameDir}");

            // ── 按 index 下文件 ──
            var files = index.Files
                .Where(f => IsNeededOnClient(f, warnings))
                .ToList();

            var done = 0;
            var failed = 0;

            // 懒人包的模组已经在压缩包里，没有要下载的东西 —— 报「0/0」会让人以为出了问题
            progress?.Report(files.Count == 0
                ? new ModpackProgress("没有要下载的文件，内容都在压缩包里", -1)
                : new ModpackProgress($"下载文件 0/{files.Count}", 0));

            await Parallel.ForEachAsync(
                files,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxParallelDownloads,
                    CancellationToken = cancellationToken,
                },
                async (file, token) =>
                {
                    var target = SafeCombine(gameDir, file.Path);
                    if (target is null)
                    {
                        lock (warnings) warnings.Add($"路径不合法，跳过：{file.Path}");
                        return;
                    }

                    if (file.Env?.Client == "unsupported")
                    {
                        // 仅服务端的文件：客户端装上去没有意义，有的还会导致启动报错
                        return;
                    }

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                        if (File.Exists(target) && file.FileSize > 0 && new FileInfo(target).Length == file.FileSize)
                        {
                            // 已经有了
                        }
                        else
                        {
                            var url = file.Downloads.FirstOrDefault()
                                      ?? throw new InvalidOperationException("没有下载地址");

                            await DownloadAsync(url, target, file.Hashes.Sha1, null, null, token)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lock (warnings) warnings.Add($"{file.Path} 下载失败：{ex.Message}");
                        Interlocked.Increment(ref failed);
                    }

                    var current = Interlocked.Increment(ref done);
                    progress?.Report(new ModpackProgress(
                        $"下载文件 {current}/{files.Count}",
                        files.Count == 0 ? 1 : (double)current / files.Count));
                }).ConfigureAwait(false);

            // ── overrides 覆盖 ──
            progress?.Report(new ModpackProgress("解压配置与默认资源", -1));

            var overrides = extractOverrides(gameDir);
            Log?.Invoke(this, $"overrides 解压 {overrides} 个文件");

            if (failed > 0)
                warnings.Add($"{failed} 个文件没下成，游戏可能缺内容。可以重启启动器后重装，已下好的不会重下");

            return new ModpackInstallResult(
                instance.Name,
                instance.Directory,
                installed.VersionName,
                minecraftVersion,
                loaderLabel,
                files.Count - failed,
                warnings);
        }
        catch
        {
            // 装到一半失败：实例留着（里面有已经下好的几十个文件，删了更亏），
            // 但要说清楚它现在是不完整的
            Log?.Invoke(this, $"整合包安装中断，实例「{instance.Name}」保留但不完整");
            throw;
        }
    }

    // ── index 解析 ───────────────────────────────────────

    /// <summary>从 <c>.mrpack</c> 里读 <c>modrinth.index.json</c>。</summary>
    private static ModpackIndex ReadIndex(string packPath)
    {
        using var archive = ZipFile.OpenRead(packPath);

        var entry = archive.GetEntry("modrinth.index.json")
                    ?? throw new InvalidOperationException(
                        "这个 .mrpack 里没有 modrinth.index.json —— 可能不是标准的 Modrinth 整合包。");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);

        var json = reader.ReadToEnd();

        return JsonSerializer.Deserialize<ModpackIndex>(json, new JsonSerializerOptions
               {
                   PropertyNameCaseInsensitive = true,
               })
               ?? throw new InvalidOperationException("modrinth.index.json 解析失败。");
    }

    /// <summary>
    /// 从 dependencies 判断加载器。
    /// </summary>
    /// <remarks>
    /// <b>Quilt 会明确报错而不是硬装</b>：本项目的加载器安装只支持原版 / Forge / NeoForge / Fabric，
    /// 假装支持只会让用户装完发现启动不了，还查不出原因。
    /// </remarks>
    private static (ModLoaderKind Kind, string Version) PickLoader(IReadOnlyDictionary<string, string> dependencies)
    {
        if (dependencies.TryGetValue("neoforge", out var neo)) return (ModLoaderKind.NeoForge, neo);
        if (dependencies.TryGetValue("forge", out var forge)) return (ModLoaderKind.Forge, forge);
        if (dependencies.TryGetValue("fabric-loader", out var fabric)) return (ModLoaderKind.Fabric, fabric);

        if (dependencies.TryGetValue("quilt-loader", out var quilt))
            throw new NotSupportedException(
                $"这个整合包要 Quilt {quilt}，本启动器还不支持 Quilt。可以试试找它的 Fabric 版本。");

        return (ModLoaderKind.Vanilla, string.Empty);
    }

    /// <summary>只处理客户端要的文件，顺手收集该跳过的原因。</summary>
    private static bool IsNeededOnClient(ModpackFile file, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(file.Path)) return false;

        if (file.Env?.Client == "unsupported")
            return false;

        if (file.Downloads.Count == 0)
        {
            warnings.Add($"{file.Path} 没有下载地址（整合包作者可能要求手动放）");
            return false;
        }

        return true;
    }

    // ── 文件操作 ─────────────────────────────────────────

    /// <summary>下载一个文件到指定路径，带 sha1 校验和 <c>.part</c> 保护。</summary>
    private async Task DownloadAsync(
        string url,
        string destination,
        string? sha1,
        string? stage,
        IProgress<ModpackProgress>? progress,
        CancellationToken cancellationToken)
    {
        var partial = destination + ".part";

        try
        {
            using var response = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);

            var buffer = new byte[81920];
            long received = 0;

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0) break;

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;

                if (stage is not null && progress is not null && total > 0)
                    progress.Report(new ModpackProgress(stage, (double)received / total));
            }

            target.Close();

            if (!string.IsNullOrWhiteSpace(sha1))
            {
                await using var check = File.OpenRead(partial);
                var actual = Convert.ToHexString(await SHA1.HashDataAsync(check, cancellationToken).ConfigureAwait(false));

                if (!string.Equals(actual, sha1.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"sha1 校验不通过：{Path.GetFileName(destination)}");
            }

            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial))
            {
                try { File.Delete(partial); } catch { /* 删不掉也没关系，.part 不会被游戏读 */ }
            }
        }
    }

    /// <summary>把压缩包里 <paramref name="prefix"/> 目录下的内容解到游戏目录。</summary>
    /// <param name="prefix">
    /// 覆盖目录名。Modrinth 固定是 <c>overrides</c>；
    /// CurseForge 由 <c>manifest.json</c> 的 <c>overrides</c> 字段指定（也可能是别的名字）。
    /// </param>
    private static int ExtractOverrides(string packPath, string prefix, string instanceDirectory)
    {
        var count = 0;

        var normalized = string.IsNullOrWhiteSpace(prefix) ? "overrides" : prefix.Trim().TrimEnd('/');
        var lookup = normalized + "/";

        using var archive = ZipFile.OpenRead(packPath);

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(lookup, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(entry.Name)) continue; // 目录项

            var relative = entry.FullName[lookup.Length..];
            var target = SafeCombine(instanceDirectory, relative);
            if (target is null) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            count++;
        }

        return count;
    }

    /// <summary>
    /// 拼路径，并挡住能跳出实例目录的写法。
    /// </summary>
    /// <remarks>
    /// index 里的 <c>path</c> 来自网络，理论上可以是 <c>../../windows/system32/x.dll</c>。
    /// 整合包里带这种路径要么是打包工具出错，要么就是恶意 —— 两种都不该写下去。
    /// </remarks>
    private static string? SafeCombine(string root, string relative)
    {
        var cleaned = relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

        if (cleaned.Contains("..", StringComparison.Ordinal)) return null;

        var full = Path.GetFullPath(Path.Combine(root, cleaned));
        var rootFull = Path.GetFullPath(root);

        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string Sanitize(string name)
    {
        var cleaned = name;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            cleaned = cleaned.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(cleaned) ? "pack.mrpack" : cleaned;
    }

    public void Dispose() => _http.Dispose();
}

// ── modrinth.index.json 的模型 ──────────────────────────

/// <summary>整合包里的 <c>modrinth.index.json</c>。</summary>
public sealed class ModpackIndex
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("versionId")]
    public string VersionId { get; init; } = string.Empty;

    /// <summary>
    /// 这个整合包依赖什么：<c>minecraft</c>、<c>fabric-loader</c>、<c>neoforge</c>、<c>forge</c>。
    /// </summary>
    /// <remarks>
    /// 值是<b>具体版本号</b>，不是范围。整合包必须钉死版本 —— 换一个加载器小版本，
    /// 几十个模组里总有某个会炸。
    /// </remarks>
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; init; } = new();

    [JsonPropertyName("files")]
    public List<ModpackFile> Files { get; init; } = new();
}

/// <summary>整合包里要下载的一个文件。</summary>
public sealed class ModpackFile
{
    /// <summary>相对实例根目录的路径，如 <c>mods/sodium.jar</c>。</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("hashes")]
    public ModrinthHashes Hashes { get; init; } = new();

    [JsonPropertyName("downloads")]
    public List<string> Downloads { get; init; } = new();

    [JsonPropertyName("fileSize")]
    public long FileSize { get; init; }

    [JsonPropertyName("env")]
    public ModpackEnv? Env { get; init; }
}

/// <summary>文件在客户端/服务端各要不要。</summary>
public sealed class ModpackEnv
{
    [JsonPropertyName("client")]
    public string Client { get; init; } = "required";

    [JsonPropertyName("server")]
    public string Server { get; init; } = "required";
}

// ── CurseForge manifest.json 的模型 ──────────────────────

/// <summary>
/// CurseForge 导出的整合包里的 <c>manifest.json</c>。
/// </summary>
/// <remarks>
/// <b>跟 Modrinth 的 index 最大的区别</b>：这里只有「项目 ID + 文件 ID」，
/// <b>没有文件名，也没有下载地址</b>。要拿到真实地址必须反查 CurseForge，
/// 所以安装前得先跑一轮查询（见 <c>ResolveCurseForgeFilesAsync</c>）。
/// </remarks>
public sealed class CurseForgeManifest
{
    [JsonPropertyName("manifestType")]
    public string ManifestType { get; init; } = "minecraftModpack";

    [JsonPropertyName("manifestVersion")]
    public int ManifestVersion { get; init; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; init; } = string.Empty;

    [JsonPropertyName("minecraft")]
    public CurseForgeMinecraft Minecraft { get; init; } = new();

    /// <summary>覆盖文件所在的目录名，通常是 <c>overrides</c>，但不保证。</summary>
    [JsonPropertyName("overrides")]
    public string Overrides { get; init; } = "overrides";

    [JsonPropertyName("files")]
    public List<CurseForgeFileRef> Files { get; init; } = new();
}

/// <summary>整合包要求的游戏版本与加载器。</summary>
public sealed class CurseForgeMinecraft
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("modLoaders")]
    public List<CurseForgeLoader> ModLoaders { get; init; } = new();
}

/// <summary>一条加载器要求，<c>id</c> 形如 <c>neoforge-21.4.135</c>。</summary>
public sealed class CurseForgeLoader
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("primary")]
    public bool Primary { get; init; }
}

/// <summary>manifest 里的一个文件引用（只有 ID，没有名字和地址）。</summary>
public sealed class CurseForgeFileRef
{
    [JsonPropertyName("projectID")]
    public int ProjectId { get; init; }

    [JsonPropertyName("fileID")]
    public int FileId { get; init; }

    [JsonPropertyName("required")]
    public bool Required { get; init; } = true;
}
