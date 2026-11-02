using System.Net;
using System.Security.Cryptography;

namespace DeepDive.Core.Modrinth;

/// <summary>下载进度（一条文件一个回调）。</summary>
/// <param name="FileName">正在下的文件名。</param>
/// <param name="Received">已收字节。</param>
/// <param name="Total">总字节。<c>0</c> 表示服务器没给长度。</param>
/// <param name="Index">第几个（从 1 开始）。</param>
/// <param name="Count">一共要下几个（含依赖）。</param>
public sealed record ContentDownloadProgress(string FileName, long Received, long Total, int Index, int Count)
{
    /// <summary>0~1 的完成度。总长度未知时返回 <c>-1</c>，界面改用不确定进度条。</summary>
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : -1;

    /// <summary>「正在下载 2/4：sodium-fabric-0.5.11.jar」。</summary>
    public string Display => $"正在下载 {Index}/{Count}：{FileName}";
}

/// <summary>一次安装的结果。</summary>
/// <param name="Folder">装到哪个目录。</param>
/// <param name="Installed">这次真的写下去的文件名。</param>
/// <param name="Skipped">本来就有、跳过没下的文件名。</param>
/// <param name="Warnings">没装成的东西和原因（比如「依赖 X 只给了项目 ID，没法自动选版本」）。</param>
public sealed record ContentInstallResult(
    string Folder,
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Warnings)
{
    public bool AnythingInstalled => Installed.Count > 0;

    /// <summary>给界面用的一句话。</summary>
    public string Summary => (Installed.Count, Skipped.Count) switch
    {
        (0, 0) => "什么都没下",
        (0, _) => $"{Skipped.Count} 个文件本来就装过，没重复下载",
        (_, 0) => $"装好了 {Installed.Count} 个文件",
        _ => $"装好了 {Installed.Count} 个文件，{Skipped.Count} 个已存在跳过",
    };
}

/// <summary>
/// 把 Modrinth 上的一个版本（连同必需的依赖）真正下到磁盘。
/// </summary>
/// <remarks>
/// <para>
/// <b>只干一件事：把文件放到游戏会读的目录里。</b> 不碰版本 JSON、不改配置、不解压整合包 ——
/// 那些是另一层的事。这样「模组、光影包、资源包」三类可以共用同一段代码，只是目标目录不同。
/// </para>
/// <para>
/// 三条硬要求：
/// </para>
/// <list type="number">
///   <item><b>先写 <c>.part</c> 再改名</b>。半截文件留在 mods 目录里会让游戏启动崩溃，
///         而崩溃信息只会说「Sodium 加载失败」，根本查不到是下载没下完。</item>
///   <item><b>sha1 对不上就删掉重来一次</b>，两次都不对就报错，绝不把坏文件留在目录里。</item>
///   <item><b>同名同大小视为已装</b>，不再下一遍（几十 MB 的光影包重下很痛）。</item>
/// </list>
/// </remarks>
public sealed class ContentInstaller : IDisposable
{
    /// <summary>依赖最多往下追几层。防环，也防某个项目的依赖树炸开。</summary>
    private const int MaxDependencyDepth = 3;

    private readonly ModrinthClient _client;
    private readonly HttpClient _http;

    public ContentInstaller(ModrinthClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));

        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            // 几十 MB 的光影包走慢线路也能有几十 KB/s，给足 5 分钟
            Timeout = TimeSpan.FromMinutes(5),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepDive/0.1.0 (Minecraft launcher; local build)");
    }

    /// <summary>
    /// 安装一个版本。
    /// </summary>
    /// <param name="version">要装的版本（<see cref="ModrinthVersion"/>）。</param>
    /// <param name="targetFolder">目标目录（mods / shaderpacks / resourcepacks）。不存在会建。</param>
    /// <param name="progress">进度回调，<b>可能在后台线程调用</b>。</param>
    /// <param name="cancellationToken">用户取消就停。</param>
    public async Task<ContentInstallResult> InstallAsync(
        ModrinthVersion version,
        string targetFolder,
        IProgress<ContentDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = new List<ModrinthVersionFile>();
        var warnings = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        // 先把「要下哪些文件」算出来，这样进度条才知道分母
        await CollectAsync(version, plan, warnings, visited, progress, cancellationToken, depth: 0)
            .ConfigureAwait(false);

        var installed = new List<string>();
        var skipped = new List<string>();

        Directory.CreateDirectory(targetFolder);

        for (var i = 0; i < plan.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = plan[i];
            var destination = Path.Combine(targetFolder, SanitizeFileName(file.FileName));

            if (IsAlreadyThere(destination, file))
            {
                skipped.Add(file.FileName);
                continue;
            }

            await DownloadFileAsync(file, destination, i + 1, plan.Count, progress, cancellationToken)
                .ConfigureAwait(false);

            installed.Add(file.FileName);
        }

        return new ContentInstallResult(targetFolder, installed, skipped, warnings);
    }

    /// <summary>递归收集文件和依赖。</summary>
    private async Task CollectAsync(
        ModrinthVersion version,
        List<ModrinthVersionFile> plan,
        List<string> warnings,
        HashSet<string> visited,
        IProgress<ContentDownloadProgress>? progress,
        CancellationToken cancellationToken,
        int depth)
    {
        if (!visited.Add(version.Id)) return;

        var primary = PickPrimaryFile(version);
        if (primary is null)
        {
            warnings.Add($"{Label(version)}：这个版本没有可下载的文件");
            return;
        }

        // 同一文件名只下一次（不同依赖偶尔会引用同一个 jar）
        if (!plan.Any(f => string.Equals(f.FileName, primary.FileName, StringComparison.OrdinalIgnoreCase)))
            plan.Add(primary);

        if (depth >= MaxDependencyDepth) return;

        var required = version.Dependencies
            .Where(d => string.Equals(d.DependencyType, "required", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var dependency in required)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(dependency.VersionId))
            {
                // 只给了项目 ID：得按游戏版本和加载器自己去挑一个，这事留给下一步。
                // 现在至少要说出来，不能静悄悄当没看见。
                warnings.Add("有一个必需依赖只标了项目、没锁定版本，需要手动装");
                continue;
            }

            try
            {
                var dependencyVersion = await _client
                    .GetVersionAsync(dependency.VersionId, cancellationToken)
                    .ConfigureAwait(false);

                if (dependencyVersion is null)
                {
                    warnings.Add($"依赖 {dependency.VersionId} 读不出来");
                    continue;
                }

                await CollectAsync(dependencyVersion, plan, warnings, visited, progress, cancellationToken,
                        depth + 1)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add($"依赖读取失败：{ex.Message}");
            }
        }
    }

    /// <summary>挑出该下的那个文件：优先 <c>primary</c>，没有就第一个。</summary>
    private static ModrinthVersionFile? PickPrimaryFile(ModrinthVersion version)
    {
        if (version.Files.Count == 0) return null;

        return version.Files.FirstOrDefault(f => f.Primary)
               ?? version.Files[0];
    }

    /// <summary>目标文件已经在、而且大小一致 —— 当它装过了。</summary>
    /// <remarks>
    /// 只比大小不比 sha1：几十 MB 的文件每次都算一遍哈希要多等好几秒，
    /// 而「同名同大小但不是同一个文件」在 mods 目录里几乎不会发生。
    /// </remarks>
    private static bool IsAlreadyThere(string destination, ModrinthVersionFile file) =>
        File.Exists(destination) && file.Size > 0 && new FileInfo(destination).Length == file.Size;

    /// <summary>下一个文件：先 <c>.part</c>，校验后再改名。</summary>
    private async Task DownloadFileAsync(
        ModrinthVersionFile file,
        string destination,
        int index,
        int count,
        IProgress<ContentDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var partial = destination + ".part";

        try
        {
            // 给两次机会：第一次可能是网络抖动导致 sha1 不对
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                await FetchAsync(file, partial, index, count, progress, cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(file.Hashes.Sha1) ||
                    await Sha1MatchesAsync(partial, file.Hashes.Sha1, cancellationToken).ConfigureAwait(false))
                {
                    File.Move(partial, destination, overwrite: true);
                    return;
                }

                File.Delete(partial);

                if (attempt == 2)
                    throw new InvalidOperationException(
                        $"「{file.FileName}」两次下载的 sha1 都对不上，文件可能已被服务器替换。稍后再试。");
            }
        }
        finally
        {
            // 取消或失败时别把半截文件留在目录里
            if (File.Exists(partial))
            {
                try { File.Delete(partial); } catch { /* 删不掉就算了，.part 游戏不认 */ }
            }
        }
    }

    /// <summary>真正搬字节的地方。</summary>
    private async Task FetchAsync(
        ModrinthVersionFile file,
        string partialPath,
        int index,
        int count,
        IProgress<ContentDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http
            .GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? file.Size;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            partialPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        var lastReport = 0L;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            // 每 256 KB 报一次就够界面刷新了，太频繁反而拖慢下载
            if (received - lastReport < 262_144 && (total <= 0 || received < total)) continue;

            lastReport = received;
            progress?.Report(new ContentDownloadProgress(file.FileName, received, total, index, count));
        }

        progress?.Report(new ContentDownloadProgress(file.FileName, received, total, index, count));
    }

    private static async Task<bool> Sha1MatchesAsync(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 文件名清洗：只去路径分隔符。
    /// </summary>
    /// <remarks>
    /// Modrinth 的文件名偶尔带空格和括号（<c>[1.21] Sodium (Fabric).jar</c>），
    /// 这些都是合法的 Windows 文件名，不能乱改 —— 改了可能被加载器认不出来。
    /// 唯一必须挡的是 <c>..\</c> 这类能跳出目标目录的东西。
    /// </remarks>
    private static string SanitizeFileName(string name)
    {
        var cleaned = name.Replace('/', '_').Replace('\\', '_').Trim();

        if (cleaned is "." or "..") return Guid.NewGuid().ToString("N") + ".jar";
        if (cleaned.Length == 0) return Guid.NewGuid().ToString("N") + ".jar";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            cleaned = cleaned.Replace(invalid, '_');

        return cleaned;
    }

    private static string Label(ModrinthVersion version) =>
        string.IsNullOrWhiteSpace(version.VersionNumber) ? version.Name : version.VersionNumber;

    public void Dispose() => _http.Dispose();
}
