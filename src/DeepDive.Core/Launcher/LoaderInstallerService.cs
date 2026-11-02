using System.Text.Json;
using System.Xml.Linq;
using CmlLib.Core;
using CmlLib.Core.Installers;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.Forge.Installers;
using CmlLib.Core.Installer.Forge.Versions;
using CmlLib.Core.ModLoaders.FabricMC;

namespace DeepDive.Core.Launcher;

/// <summary>
/// Forge / NeoForge / Fabric 的发现与安装。
/// </summary>
/// <remarks>
/// <para><b>为什么不直接用 CmlLib 自带的 ForgeInstaller.Install()：</b>
/// 它每一个 <c>Install</c> 重载最后都会走 <c>_runner.Install(..., showAd)</c>，
/// 而 <c>showAd</c> 里是 <c>Process.Start("https://adfoc.us/...")</c> —— 装个加载器会弹广告页。
/// <c>ForgeInstallOptions</c> 里没有开关能关掉它。所以这里改成自己拼装：
/// 用 public 的 <see cref="ForgeInstallerVersionMapper"/> 造出 <c>IForgeInstaller</c>，
/// 直接调 <c>installer.Install(...)</c>，跳过那一层包装。NeoForge 的官方入口本来就不带广告，直接用。</para>
///
/// <para><b>为什么 Forge 版本不走 <c>ForgeVersionLoader</c>：</b>
/// 那个类拿版本是靠 <b>爬 files.minecraftforge.net 的 HTML</b>，XPath 写死成
/// <c>//html[1]//body[1]//main[1]//div[2]//div[2]//div[2]//table[1]//tbody[1]//tr</c>，
/// 官网改一次版式就全废。这里改用 Forge Maven 的 <c>maven-metadata.xml</c>（XML，稳定）
/// 取全量版本，再用官方的 <c>promotions_slim.json</c> 标出推荐版。</para>
///
/// <para><b>安装后必须补一次 <c>InstallAsync</c>：</b>加载器安装器只写版本 JSON 和库，
/// 原版 jar / assets / natives 还得靠正常的安装流程补齐。</para>
/// </remarks>
public sealed class LoaderInstallerService : IDisposable
{
    private const string ForgeMavenRoot = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string ForgePromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

    /// <summary>
    /// NeoForge 的 Maven 根。
    /// </summary>
    /// <remarks>
    /// ⚠️ 这个域名在国内 <b>DNS 被污染</b>。2026-10-29 实测 <c>maven.neoforged.net</c>
    /// 解析到 <c>199.59.150.43</c> / <c>2a03:2880:...:face:b00c</c> —— 都是 Twitter 的段，
    /// 直连必然超时（10 秒无响应）。所以 NeoForge 这条路<b>强制走 BMCLAPI 镜像</b>，
    /// 不走 <see cref="MinecraftLauncherService"/> 当前选定的源。
    /// </remarks>
    private const string NeoForgeMavenRoot = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

    private readonly MinecraftLauncherService _core;

    /// <summary>专供 NeoForge 的 HttpClient：强制带 BMCLAPI 前缀重写。</summary>
    private readonly HttpClient _neoHttp;

    /// <summary>专供 NeoForge 的下载器（installer jar 也得从镜像拿）。</summary>
    private readonly ParallelGameInstaller _neoInstaller;

    /// <summary>文件级进度（下载加载器相关文件时）。</summary>
    public event EventHandler<InstallerProgressChangedEventArgs>? FileProgressChanged;

    /// <summary>字节级进度。</summary>
    public event EventHandler<ByteProgress>? ByteProgressChanged;

    /// <summary>人可读日志（含安装器自己的 stdout）。</summary>
    public event EventHandler<string>? Log;

    public LoaderInstallerService(MinecraftLauncherService core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));

        // 元数据请求要快速失败：15 秒 × 2 次就够判断镜像通不通，然后回退官方源。
        var (http, _) = MirrorConfigurator.Create(useMirror: true, attemptTimeoutSeconds: 15, maxAttempts: 2);
        _neoHttp = http;
        _neoInstaller = new ParallelGameInstaller(
            maxChecker: MirrorConfigurator.MaxCheckers,
            maxDownloader: MirrorConfigurator.MaxDownloaders,
            boundedCapacity: MirrorConfigurator.BoundedCapacity,
            httpClient: _neoHttp);

        // 原版下载那一大段是 _core.InstallAsync 干的，进度得从这里转出去，
        // 否则 UI 上「下原版」期间会一直是 0%。
        _core.FileProgressChanged += OnCoreFileProgress;
        _core.ByteProgressChanged += OnCoreByteProgress;
        _core.Log += OnCoreLog;
    }

    private void OnCoreFileProgress(object? sender, InstallerProgressChangedEventArgs e)
        => FileProgressChanged?.Invoke(this, e);

    private void OnCoreByteProgress(object? sender, ByteProgress e)
        => ByteProgressChanged?.Invoke(this, e);

    private void OnCoreLog(object? sender, string message)
        => Log?.Invoke(this, message);

    // ───────────────────────── 版本发现 ─────────────────────────

    /// <summary>
    /// 取一段文本：先走镜像，镜像不行再回退到当前下载源（官方）。
    /// </summary>
    /// <remarks>
    /// 镜像优先是因为本机实测：<c>maven.minecraftforge.net</c> 直连三次里两次 20 秒连不上，
    /// <c>maven.neoforged.net</c> 更是 DNS 被污染到 Twitter 的 IP。镜像延迟高一点（6 秒 vs 2.7 秒），
    /// 但成功率是 100% 对 33%。速度输一点，稳定性赢一大截。
    /// </remarks>
    private async Task<string> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        string mirrorFailure;
        try
        {
            return await _neoHttp.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            mirrorFailure = Short(ex.Message);
        }

        Log?.Invoke(this, $"（镜像取不到，改试官方源：{mirrorFailure}）");
        return await _core.Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>列出某加载器可用于指定 MC 版本的全部构建，按版本号从新到旧。</summary>
    public async Task<IReadOnlyList<LoaderVersionInfo>> GetLoaderVersionsAsync(
        ModLoaderKind kind,
        string minecraftVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftVersion);

        return kind switch
        {
            ModLoaderKind.Vanilla => Array.Empty<LoaderVersionInfo>(),
            ModLoaderKind.Forge => await GetForgeVersionsAsync(minecraftVersion, cancellationToken).ConfigureAwait(false),
            ModLoaderKind.NeoForge => await GetNeoForgeVersionsAsync(minecraftVersion, cancellationToken).ConfigureAwait(false),
            ModLoaderKind.Fabric => await GetFabricVersionsAsync(minecraftVersion, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>Forge：Maven 全量版本 + 官方 promotions 标推荐。</summary>
    private async Task<IReadOnlyList<LoaderVersionInfo>> GetForgeVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var loaderVersions = await FetchForgeMavenVersionsAsync(minecraftVersion, cancellationToken)
            .ConfigureAwait(false);

        if (loaderVersions.Count == 0)
        {
            // 1.21.0 这种写法在 Maven 里可能是 1.21，反过来也可能；试一次去掉 .0
            if (minecraftVersion.EndsWith(".0", StringComparison.Ordinal))
            {
                var trimmed = minecraftVersion[..^2];
                Log?.Invoke(this, $"{minecraftVersion} 在 Forge Maven 里没有条目，改用 {trimmed} 再查一次");
                loaderVersions = await FetchForgeMavenVersionsAsync(trimmed, cancellationToken).ConfigureAwait(false);
                if (loaderVersions.Count > 0)
                    minecraftVersion = trimmed;
            }

            if (loaderVersions.Count == 0)
                return Array.Empty<LoaderVersionInfo>();
        }

        var (recommended, latest) = await FetchForgePromotionsAsync(minecraftVersion, cancellationToken)
            .ConfigureAwait(false);

        return loaderVersions
            .Select(v => new LoaderVersionInfo(
                v,
                IsRecommended: recommended is not null && v == recommended,
                IsLatest: latest is not null && v == latest))
            .ToList();
    }

    private async Task<List<string>> FetchForgeMavenVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var url = $"{ForgeMavenRoot}/maven-metadata.xml";
        Log?.Invoke(this, $"读取 Forge 版本清单：{url}");

        var xml = await FetchTextAsync(url, cancellationToken).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);

        var prefix = minecraftVersion + "-";
        var result = new List<string>();
        foreach (var element in doc.Descendants("version"))
        {
            var full = element.Value?.Trim();
            if (string.IsNullOrEmpty(full) || !full.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var loader = full[prefix.Length..];
            if (loader.Length > 0 && !result.Contains(loader, StringComparer.OrdinalIgnoreCase))
                result.Add(loader);
        }

        // 版本号从新到旧（按数字段比较，"47.4.28" 必须排在 "47.4.9" 前面）
        result.Sort((a, b) => CompareVersionDescending(a, b));
        return result;
    }

    private async Task<(string? Recommended, string? Latest)> FetchForgePromotionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = await FetchTextAsync(ForgePromotionsUrl, cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(text);

            if (!json.RootElement.TryGetProperty("promos", out var promos))
                return (null, null);

            return (
                ReadPromo(promos, $"{minecraftVersion}-recommended"),
                ReadPromo(promos, $"{minecraftVersion}-latest"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // promotions 只是锦上添花，拿不到不影响版本列表
            Log?.Invoke(this, $"（推荐版信息没取到，不影响安装：{Short(ex.Message)}）");
            return (null, null);
        }

        static string? ReadPromo(JsonElement promos, string key)
            => promos.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>NeoForge：Maven 全量版本 + 官方映射器换算 MC 版本。</summary>
    /// <remarks>
    /// 不用 <c>NeoForgeInstaller.GetNeoForgeVersions()</c>，因为那个走
    /// <c>maven.neoforged.net/api/maven/versions/...</c> —— 域名被污染，直连必超时；
    /// 而 BMCLAPI 镜像恰好<b>没有</b>镜像这个 API 端点（实测 404），却镜像了 Maven 目录本身。
    /// 所以改成拉 <c>maven-metadata.xml</c>，再用官方的 <see cref="NeoForgeVersionMapper"/>
    /// 把版本号换算成 MC 版本（它连 26.x 日历版本和 0.x 快照版都处理了）。
    /// </remarks>
    private async Task<IReadOnlyList<LoaderVersionInfo>> GetNeoForgeVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var url = $"{NeoForgeMavenRoot}/maven-metadata.xml";
        Log?.Invoke(this, $"读取 NeoForge 版本清单（经镜像）：{url}");

        var xml = await FetchTextAsync(url, cancellationToken).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);
        var wanted = NeoForgeVersionMapper.NormalizeMinecraftVersion(minecraftVersion);

        var matched = new List<string>();
        foreach (var element in doc.Descendants("version"))
        {
            var name = element.Value?.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            string mappedMc;
            try
            {
                mappedMc = NeoForgeVersionMapper.MapMinecraftVersion(name);
            }
            catch (FormatException)
            {
                // 快照版等映射不出来的，直接跳过 —— 不是给普通玩家用的
                continue;
            }

            if (mappedMc == wanted && !matched.Contains(name, StringComparer.OrdinalIgnoreCase))
                matched.Add(name);
        }

        matched.Sort((a, b) => CompareVersionDescending(a, b));
        return matched.Select((v, i) => new LoaderVersionInfo(v, IsLatest: i == 0)).ToList();
    }

    /// <summary>Fabric：走官方 meta API（镜像优先，失败回退官方）。</summary>
    private async Task<IReadOnlyList<LoaderVersionInfo>> GetFabricVersionsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<FabricLoader> loaders;
        try
        {
            loaders = await new FabricInstaller(_neoHttp)
                .GetLoaders(minecraftVersion)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"（镜像取 Fabric 版本失败，改试官方：{Short(ex.Message)}）");
            loaders = await new FabricInstaller(_core.Http)
                .GetLoaders(minecraftVersion)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var list = loaders
            .Where(l => !string.IsNullOrEmpty(l.Version))
            .Select(l => new { l.Version, l.Stable })
            .ToList();

        if (list.Count == 0)
            return Array.Empty<LoaderVersionInfo>();

        return list
            .Select((l, i) => new LoaderVersionInfo(l.Version!, IsLatest: i == 0))
            .ToList();
    }

    // ───────────────────────── 安装 ─────────────────────────

    /// <summary>
    /// 安装加载器（或只装原版）。<paramref name="loaderVersion"/> 为空时自动取推荐版 / 最新版。
    /// </summary>
    /// <returns>版本名，可直接拿去 <c>LaunchAsync</c>。</returns>
    public async Task<LoaderInstallResult> InstallAsync(
        ModLoaderKind kind,
        string minecraftVersion,
        string? loaderVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftVersion);

        // 安装器（尤其 Forge/NeoForge 的官方 java installer）会把中间文件写到系统临时目录，
        // 那地方在某些环境下不可写，会在安装到一半时抛 UnauthorizedAccessException。
        // 整个安装过程统一改用「实例根\temp」。
        using var tempScope = TempDirectoryScope.Activate(LauncherPaths.DataRoot);

        // 三个加载器都先要原版 + Java 就位：
        // 原版 jar 是加载器打补丁的底子，Java 是执行官方 installer.jar 的前提。
        // 本机初始一个 Java 都没有，靠 InstallAsync 里的 JavaFileExtractor 拉 JRE。
        Log?.Invoke(this, $"准备原版 {minecraftVersion}（含 Java 运行时）…");
        await _core.InstallAsync(minecraftVersion, cancellationToken).ConfigureAwait(false);

        Log?.Invoke(this, $"临时目录：{tempScope.Path}");

        return kind switch
        {
            ModLoaderKind.Vanilla => new LoaderInstallResult(
                minecraftVersion, ModLoaderKind.Vanilla, minecraftVersion, string.Empty),
            ModLoaderKind.Forge => await InstallForgeAsync(minecraftVersion, loaderVersion, cancellationToken).ConfigureAwait(false),
            ModLoaderKind.NeoForge => await InstallNeoForgeAsync(minecraftVersion, loaderVersion, cancellationToken).ConfigureAwait(false),
            ModLoaderKind.Fabric => await InstallFabricAsync(minecraftVersion, loaderVersion, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private async Task<LoaderInstallResult> InstallForgeAsync(
        string minecraftVersion,
        string? loaderVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(loaderVersion))
        {
            var available = await GetLoaderVersionsAsync(ModLoaderKind.Forge, minecraftVersion, cancellationToken)
                .ConfigureAwait(false);
            if (available.Count == 0)
                throw new InvalidOperationException($"Forge 没有支持 Minecraft {minecraftVersion} 的版本");

            loaderVersion = (available.FirstOrDefault(v => v.IsRecommended) ?? available[0]).LoaderVersion;
            Log?.Invoke(this, $"未指定 Forge 版本，自动选用 {loaderVersion}");
        }

        var javaPath = await ResolveJavaPathAsync(minecraftVersion, cancellationToken).ConfigureAwait(false);

        // 自己造 ForgeVersion：CreateInstaller 会按 MC 版本自动选 V5 / V7 / V12 分支。
        // 关键是 DirectUrl 直接指向 Maven，绕开官网的 AdUrl 跳转。
        var installerUrl =
            $"{ForgeMavenRoot}/{minecraftVersion}-{loaderVersion}/forge-{minecraftVersion}-{loaderVersion}-installer.jar";
        var forgeVersion = new ForgeVersion(minecraftVersion, loaderVersion)
        {
            Files = new[] { new ForgeVersionFile { Type = "installer", DirectUrl = installerUrl } },
        };

        var installer = new ForgeInstallerVersionMapper().CreateInstaller(forgeVersion);
        Log?.Invoke(this, $"运行 Forge 安装器（{installer.VersionName}）…");

        // installer.jar 和它要拉的库都在 maven.minecraftforge.net / libraries.minecraft.net 上，
        // 前者直连极不稳，所以先用镜像下载器；整趟失败再退回当前源重来一次。
        try
        {
            await installer
                .Install(_core.GamePath, _neoInstaller, BuildOptions(javaPath, cancellationToken))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log?.Invoke(this, $"镜像安装失败（{Short(ex.Message)}），改用当前下载源重来一次");
            await installer
                .Install(_core.GamePath, _core.Launcher.GameInstaller, BuildOptions(javaPath, cancellationToken))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await FinishLoaderInstallAsync(installer.VersionName, cancellationToken).ConfigureAwait(false);
        return new LoaderInstallResult(installer.VersionName, ModLoaderKind.Forge, minecraftVersion, loaderVersion);
    }

    private async Task<LoaderInstallResult> InstallNeoForgeAsync(
        string minecraftVersion,
        string? loaderVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(loaderVersion))
        {
            var available = await GetLoaderVersionsAsync(ModLoaderKind.NeoForge, minecraftVersion, cancellationToken)
                .ConfigureAwait(false);
            if (available.Count == 0)
                throw new InvalidOperationException($"NeoForge 没有支持 Minecraft {minecraftVersion} 的版本");

            loaderVersion = available[0].LoaderVersion;
            Log?.Invoke(this, $"未指定 NeoForge 版本，自动选用 {loaderVersion}");
        }

        var javaPath = await ResolveJavaPathAsync(minecraftVersion, cancellationToken).ConfigureAwait(false);
        var normalizedMc = NeoForgeVersionMapper.NormalizeMinecraftVersion(minecraftVersion);

        // 复刻 NeoForgeInstaller.Install 内部那几步，但换成镜像地址 + 镜像下载器。
        var artifact = new ForgeV12VersionArtifact(
            MinecraftVersion: normalizedMc,
            LoaderVersion: loaderVersion,
            VersionName: $"neoforge-{loaderVersion}",
            InstallerUrl: $"{NeoForgeMavenRoot}/{loaderVersion}/neoforge-{loaderVersion}-installer.jar",
            EmbeddedVersionJar: null);

        var installer = new ForgeV12Installer(artifact);
        Log?.Invoke(this, $"运行 NeoForge 安装器（{installer.VersionName}）…");

        await installer
            .Install(_core.GamePath, _neoInstaller, BuildOptions(javaPath, cancellationToken))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        await FinishLoaderInstallAsync(installer.VersionName, cancellationToken).ConfigureAwait(false);
        return new LoaderInstallResult(installer.VersionName, ModLoaderKind.NeoForge, minecraftVersion, loaderVersion);
    }

    private async Task<LoaderInstallResult> InstallFabricAsync(
        string minecraftVersion,
        string? loaderVersion,
        CancellationToken cancellationToken)
    {
        // profile JSON 从 meta.fabricmc.net 取，镜像能通就走镜像
        var installer = new FabricInstaller(_neoHttp);

        Log?.Invoke(this, string.IsNullOrEmpty(loaderVersion)
            ? $"运行 Fabric 安装器（自动选最新 loader）…"
            : $"运行 Fabric 安装器（{loaderVersion}）…");

        // Fabric 安装器只写版本 JSON，不下载任何文件 —— 后续那次 InstallAsync 才是真正干活的。
        var versionName = string.IsNullOrEmpty(loaderVersion)
            ? await installer.Install(minecraftVersion, _core.GamePath).WaitAsync(cancellationToken).ConfigureAwait(false)
            : await installer.Install(minecraftVersion, loaderVersion!, _core.GamePath).WaitAsync(cancellationToken).ConfigureAwait(false);

        loaderVersion ??= ExtractFabricLoaderVersion(versionName, minecraftVersion);

        await FinishLoaderInstallAsync(versionName, cancellationToken).ConfigureAwait(false);
        return new LoaderInstallResult(versionName, ModLoaderKind.Fabric, minecraftVersion, loaderVersion);
    }

    /// <summary>
    /// 加载器装完后的收尾：补齐原版文件 + 刷新内存里的版本表。
    /// </summary>
    /// <remarks>
    /// 少这一步会踩计划里点名那个坑：加载器安装器只写版本 JSON 和库，
    /// 游戏 jar / assets / natives 缺一块，启动时直接报 ClassNotFound 或缺资源。
    /// </remarks>
    private async Task FinishLoaderInstallAsync(string versionName, CancellationToken cancellationToken)
    {
        Log?.Invoke(this, $"{versionName} 的加载器文件已就位，补齐剩余文件…");
        await _core.InstallAsync(versionName, cancellationToken).ConfigureAwait(false);

        // 让 MinecraftLauncher 重新扫一遍 versions/，否则 BuildProcessAsync 找不到新版本
        try
        {
            await _core.Launcher.GetAllVersionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log?.Invoke(this, $"（刷新版本表失败，不影响启动：{Short(ex.Message)}）");
        }

        Log?.Invoke(this, $"✅ {versionName} 安装完成");
    }

    private ForgeInstallOptions BuildOptions(string javaPath, CancellationToken cancellationToken)
    {
        return new ForgeInstallOptions
        {
            JavaPath = javaPath,
            CancellationToken = cancellationToken,
            // 重装时跳过已完成的部分，省得每次都把 installer.jar 再跑一遍
            SkipIfAlreadyInstalled = true,
            FileProgress = new Progress<InstallerProgressChangedEventArgs>(
                e => FileProgressChanged?.Invoke(this, e)),
            ByteProgress = new Progress<ByteProgress>(
                e => ByteProgressChanged?.Invoke(this, e)),
            InstallerOutput = new Progress<string>(
                line => { if (!string.IsNullOrWhiteSpace(line)) Log?.Invoke(this, "  │ " + line.Trim()); }),
        };
    }

    /// <summary>算出该版本该用哪个 Java 跑安装器。</summary>
    /// <remarks>
    /// 优先用设置页里指定的那个（和启动游戏保持同一个 Java，
    /// 否则会出现「安装器能跑、游戏起不来」这种自相矛盾的现象）。
    /// </remarks>
    private async Task<string> ResolveJavaPathAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        if (LauncherSettings.ResolveJavaPath(_core.InstanceRoot) is { } configured)
        {
            Log?.Invoke(this, $"使用指定 Java：{configured}");
            return configured;
        }

        var version = await _core.Launcher.GetVersionAsync(minecraftVersion, cancellationToken).ConfigureAwait(false);

        var javaPath = _core.Launcher.GetJavaPath(version);
        if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
            javaPath = _core.Launcher.GetDefaultJavaPath();

        if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
            throw new InvalidOperationException(
                $"找不到可用的 Java，无法执行加载器安装器（{minecraftVersion}）。" +
                "正常情况下上一步 InstallAsync 会自动下载 JRE，请检查日志里 Java 那部分是否失败。");

        Log?.Invoke(this, $"使用 Java：{javaPath}");
        return javaPath;
    }

    /// <summary>从 <c>fabric-loader-0.16.9-1.21.1</c> 里抠出 loader 版本号。</summary>
    private static string ExtractFabricLoaderVersion(string versionName, string minecraftVersion)
    {
        const string head = "fabric-loader-";
        var body = versionName.StartsWith(head, StringComparison.Ordinal)
            ? versionName[head.Length..]
            : versionName;

        var tail = "-" + minecraftVersion;
        return body.EndsWith(tail, StringComparison.Ordinal)
            ? body[..^tail.Length]
            : body;
    }

    // ───────────────────────── 工具 ─────────────────────────

    /// <summary>按数字段比较版本号，返回降序用的比较结果（新版本排前面）。</summary>
    private static int CompareVersionDescending(string a, string b)
    {
        var pa = a.Split('.', '-', '+');
        var pb = b.Split('.', '-', '+');
        var count = Math.Min(pa.Length, pb.Length);

        for (var i = 0; i < count; i++)
        {
            var isNumA = int.TryParse(pa[i], out var numA);
            var isNumB = int.TryParse(pb[i], out var numB);

            if (isNumA && isNumB)
            {
                if (numA != numB) return numB.CompareTo(numA);   // 降序
            }
            else
            {
                var cmp = string.Compare(pb[i], pa[i], StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
            }
        }

        return pb.Length.CompareTo(pa.Length);
    }

    private static string Short(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "(无消息)";
        var one = message.Split('\n')[0].Trim();
        return one.Length <= 160 ? one : one[..160] + "…";
    }

    public void Dispose()
    {
        _core.FileProgressChanged -= OnCoreFileProgress;
        _core.ByteProgressChanged -= OnCoreByteProgress;
        _core.Log -= OnCoreLog;
        _neoHttp.Dispose();
    }
}
