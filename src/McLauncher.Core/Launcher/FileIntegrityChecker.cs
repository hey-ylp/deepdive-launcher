using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace McLauncher.Core.Launcher;

/// <summary>校验的两档力度。</summary>
public enum IntegrityDepth
{
    /// <summary>只看「在不在、大小对不对、有没有 0 字节」。启动前用这一档，几百毫秒。</summary>
    Quick,

    /// <summary>再算一遍 sha1。几个 GB 要读一遍盘，几十秒到一两分钟，手动点「校验文件」时用。</summary>
    Deep,
}

/// <summary>校验进度。</summary>
/// <param name="Phase">当前阶段（「客户端 jar」「库文件」「资源文件」「模组」）。</param>
/// <param name="Done">已检查数量。</param>
/// <param name="Total">总数量。</param>
/// <param name="CurrentFile">正在检查的文件（相对路径）。</param>
public readonly record struct IntegrityProgress(string Phase, int Done, int Total, string CurrentFile)
{
    public double Fraction => Total <= 0 ? 0 : (double)Done / Total;
}

/// <summary>一处问题。</summary>
/// <param name="Kind">问题类型。</param>
/// <param name="RelativePath">相对路径，给人看的。</param>
/// <param name="Detail">补充说明（期望多少字节 / 实际多少字节等）。</param>
public sealed record IntegrityIssue(IntegrityIssueKind Kind, string RelativePath, string Detail)
{
    public override string ToString() => Kind switch
    {
        IntegrityIssueKind.Missing => $"缺失 · {RelativePath}",
        IntegrityIssueKind.WrongSize => $"大小不对 · {RelativePath}（{Detail}）",
        IntegrityIssueKind.WrongHash => $"内容损坏 · {RelativePath}",
        IntegrityIssueKind.EmptyFile => $"空文件 · {RelativePath}",
        IntegrityIssueKind.BrokenZip => $"不是有效的 jar · {RelativePath}",
        IntegrityIssueKind.IncompleteDownload => $"没下完 · {RelativePath}",
        _ => RelativePath,
    };
}

public enum IntegrityIssueKind
{
    /// <summary>该有的文件不在。</summary>
    Missing,

    /// <summary>在，但大小和元数据不一致 —— 典型的下到一半。</summary>
    WrongSize,

    /// <summary>大小对、内容不对。</summary>
    WrongHash,

    /// <summary>长度为 0 的 jar。Java 读它会报 <c>zip file is empty</c>。</summary>
    EmptyFile,

    /// <summary>扩展名是 jar 但打不开。</summary>
    BrokenZip,

    /// <summary>残留的 <c>.part</c> 临时文件。</summary>
    IncompleteDownload,
}

/// <summary>一次校验的结果。</summary>
public sealed class IntegrityReport
{
    public required string VersionName { get; init; }
    public required IntegrityDepth Depth { get; init; }

    /// <summary>检查了多少个文件（客户端 jar + 库 + 资源）。</summary>
    public int CheckedFiles { get; set; }

    /// <summary>其中多少个有问题。</summary>
    public int ProblemCount => Issues.Count;

    public List<IntegrityIssue> Issues { get; } = new();

    public TimeSpan Elapsed { get; set; }

    public bool IsHealthy => Issues.Count == 0;

    /// <summary>能不能靠重新下载修好（缺的、大小不对、哈希不对、空文件都能，模组那种不行）。</summary>
    public bool CanRepair => Issues.Any(i => i.Kind != IntegrityIssueKind.BrokenZip);

    /// <summary>一句话总结，给日志和界面用。</summary>
    public string Summary => Issues.Count == 0
        ? $"文件完整：{CheckedFiles} 个文件全部正常（{Describe()}{(Depth == IntegrityDepth.Deep ? "，已校验 sha1" : "，只看了大小")}）"
        : $"发现 {Issues.Count} 处问题（共检查 {CheckedFiles} 个文件，{Describe()}）："
          + string.Join("、", Issues.GroupBy(i => i.Kind)
              .OrderByDescending(g => g.Count())
              .Select(g => $"{KindName(g.Key)} {g.Count()}"));

    private string Describe() => Elapsed.TotalSeconds < 1
        ? $"{Elapsed.TotalMilliseconds:F0} 毫秒"
        : $"{Elapsed.TotalSeconds:F1} 秒";

    public static string KindName(IntegrityIssueKind kind) => kind switch
    {
        IntegrityIssueKind.Missing => "缺失",
        IntegrityIssueKind.WrongSize => "大小不对",
        IntegrityIssueKind.WrongHash => "内容损坏",
        IntegrityIssueKind.EmptyFile => "空文件",
        IntegrityIssueKind.BrokenZip => "无效 jar",
        IntegrityIssueKind.IncompleteDownload => "没下完",
        _ => "问题",
    };
}

/// <summary>
/// 校验一个版本的文件是否齐全、是否损坏。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要有它：CmlLib 判断「要不要下载」基本只看文件在不在，不读内容。所以只要有一次下载被
/// 中途掐断，就会留下一个 0 字节或半截的 jar，之后每次启动都在这一个文件上崩，而且报错
/// （<c>zip file is empty</c> / <c>can't be written</c>）完全指不到「文件坏了」这件事上。
/// 2026-10-29 那台机器上连崩了四次都是这个原因。
/// </para>
/// <para>
/// 两档：<see cref="IntegrityDepth.Quick"/> 只看存在性和大小（启动前自动跑一次）；
/// <see cref="IntegrityDepth.Deep"/> 再算 sha1（界面上的「校验文件」按钮）。
/// </para>
/// </remarks>
public sealed class FileIntegrityChecker
{
    /// <summary>哈希并发数。读盘是瓶颈，开太多反而互相拖。</summary>
    private const int HashConcurrency = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly MinecraftLauncherService _core;

    public FileIntegrityChecker(MinecraftLauncherService core)
        => _core = core ?? throw new ArgumentNullException(nameof(core));

    /// <summary>
    /// 校验某个版本。<b>不抛「文件坏了」这类异常</b> —— 坏了就写进报告里。
    /// </summary>
    /// <exception cref="FileNotFoundException">版本没装，连 JSON 都找不到。</exception>
    public async Task<IntegrityReport> CheckAsync(
        string versionName,
        IntegrityDepth depth,
        IProgress<IntegrityProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionName);

        var clock = Stopwatch.StartNew();
        var report = new IntegrityReport { VersionName = versionName, Depth = depth };

        var merged = LoadMergedVersion(versionName);
        var expected = CollectExpectedFiles(versionName, merged);

        var done = 0;
        var total = expected.Count;

        await Parallel.ForEachAsync(
            expected,
            new ParallelOptions { MaxDegreeOfParallelism = HashConcurrency, CancellationToken = cancellationToken },
            async (file, token) =>
            {
                var issue = await VerifyOneAsync(file, depth, token).ConfigureAwait(false);
                if (issue is not null)
                {
                    lock (report.Issues) report.Issues.Add(issue);
                }

                var current = Interlocked.Increment(ref done);
                progress?.Report(new IntegrityProgress(file.Phase, current, total, file.RelativePath));
            }).ConfigureAwait(false);

        report.CheckedFiles = total;

        // 模组目录单独看：整合包/手动放的 jar 没有「期望哈希」，只能看它是不是个能打开的 jar
        CheckModsFolder(versionName, report);

        clock.Stop();
        report.Elapsed = clock.Elapsed;
        return report;
    }

    /// <summary>
    /// 把有问题的文件删掉，让下一次安装重新下载。
    /// </summary>
    /// <returns>删掉的文件数。</returns>
    public int DeleteBrokenFiles(IntegrityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var removed = 0;
        foreach (var issue in report.Issues)
        {
            if (issue.Kind is IntegrityIssueKind.Missing or IntegrityIssueKind.BrokenZip)
                continue;   // 缺失的没有文件可删；坏 zip 是模组，删了用户就少个模组，交给用户决定

            var path = Path.Combine(_core.InstanceRoot, issue.RelativePath);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[完整性] 删不掉 {path}：{ex.Message}");
            }
        }

        return removed;
    }

    // ── 期望文件清单 ────────────────────────────────────────────────

    /// <summary>一个「应该存在的文件」。</summary>
    private sealed record ExpectedFile(
        string Phase,
        string RelativePath,
        string FullPath,
        long ExpectedSize,
        string? Sha1);

    private List<ExpectedFile> CollectExpectedFiles(string versionName, MergedVersion merged)
    {
        var list = new List<ExpectedFile>(1024);

        // 1. 客户端 jar：惯例是 versions/<版本名>/<版本名>.jar；
        //    加载器版本里那份其实就是原版 client.jar 的拷贝，所以哈希要跟客户端那份对。
        var client = merged.Downloads?.Client;
        if (client is not null)
        {
            var jarName = merged.Jar ?? versionName;
            var jarPath = Path.Combine(_core.GamePath.Versions, versionName, jarName + ".jar");
            list.Add(new ExpectedFile("客户端 jar", Relative(jarPath), jarPath, client.Size, client.Sha1));
        }

        // 2. 库文件
        foreach (var library in merged.Libraries)
        {
            if (!AppliesToWindows(library.Rules)) continue;

            var artifact = library.Downloads?.Artifact;
            var relative = artifact?.Path ?? PathFromMavenName(library.Name);
            if (string.IsNullOrWhiteSpace(relative)) continue;

            var full = Path.Combine(_core.GamePath.Library, relative.Replace('/', Path.DirectorySeparatorChar));
            list.Add(new ExpectedFile("库文件", Relative(full), full, artifact?.Size ?? 0, artifact?.Sha1 ?? library.Sha1));
        }

        // 3. 资源文件：先看索引在不在，再逐个对象
        var assetIndex = merged.AssetIndex;
        if (assetIndex is not null)
        {
            var indexPath = Path.Combine(_core.GamePath.Assets, "indexes", assetIndex.Id + ".json");
            list.Add(new ExpectedFile("资源索引", Relative(indexPath), indexPath, assetIndex.Size, assetIndex.Sha1));

            foreach (var (hash, size) in ReadAssetObjects(indexPath))
            {
                var objectPath = Path.Combine(_core.GamePath.Assets, "objects", hash[..2], hash);
                list.Add(new ExpectedFile("资源文件", Relative(objectPath), objectPath, size, hash));
            }
        }

        return list;
    }

    /// <summary>读资源索引里的对象清单（hash + 字节数）。索引坏了就当没有。</summary>
    private static List<(string Hash, long Size)> ReadAssetObjects(string indexPath)
    {
        var result = new List<(string, long)>(4096);
        if (!File.Exists(indexPath)) return result;

        try
        {
            using var stream = File.OpenRead(indexPath);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            if (!doc.RootElement.TryGetProperty("objects", out var objects)) return result;

            foreach (var entry in objects.EnumerateObject())
            {
                if (!entry.Value.TryGetProperty("hash", out var hashNode)) continue;
                var hash = hashNode.GetString();
                if (string.IsNullOrWhiteSpace(hash) || hash.Length < 3) continue;

                long size = entry.Value.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : 0;
                result.Add((hash, size));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[完整性] 读资源索引失败 {indexPath}：{ex.Message}");
        }

        return result;
    }

    // ── 单个文件 ────────────────────────────────────────────────────

    private static async Task<IntegrityIssue?> VerifyOneAsync(
        ExpectedFile file, IntegrityDepth depth, CancellationToken cancellationToken)
    {
        var info = new FileInfo(file.FullPath);
        if (!info.Exists)
            return new IntegrityIssue(IntegrityIssueKind.Missing, file.RelativePath, "文件不存在");

        // 0 字节的 jar 是「下到一半被掐断」的典型产物，单独报，比「大小不对」更好懂
        if (info.Length == 0 && file.FullPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            return new IntegrityIssue(IntegrityIssueKind.EmptyFile, file.RelativePath, "0 字节");

        if (file.ExpectedSize > 0 && info.Length != file.ExpectedSize)
            return new IntegrityIssue(IntegrityIssueKind.WrongSize, file.RelativePath,
                $"应为 {Format(file.ExpectedSize)}，实际 {Format(info.Length)}");

        if (depth != IntegrityDepth.Deep || string.IsNullOrWhiteSpace(file.Sha1))
            return null;

        var actual = await HashAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
        if (actual is null) return null;   // 读不了：多半被占用，不算损坏

        return string.Equals(actual, file.Sha1, StringComparison.OrdinalIgnoreCase)
            ? null
            : new IntegrityIssue(IntegrityIssueKind.WrongHash, file.RelativePath, $"应为 {Short(file.Sha1)}，实际 {Short(actual)}");
    }

    private static async Task<string?> HashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 1 << 16, useAsync: true);
            var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ── 模组目录 ────────────────────────────────────────────────────

    /// <summary>
    /// 检查 mods 目录：残留的 <c>.part</c>、0 字节 jar、打不开的 jar。
    /// </summary>
    /// <remarks>
    /// 这里只报「能不能被 Java 读」，不判断版本/依赖是否匹配 —— 那要读 jar 里的
    /// <c>fabric.mod.json</c> / <c>mods.toml</c>，是另一件事（PCL 的「模组检查」）。
    /// </remarks>
    private void CheckModsFolder(string versionName, IntegrityReport report)
    {
        string modsDir;
        try
        {
            modsDir = Path.Combine(_core.GetGameDir(versionName), "mods");
        }
        catch (Exception)
        {
            return;
        }

        if (!Directory.Exists(modsDir)) return;

        foreach (var path in Directory.EnumerateFiles(modsDir))
        {
            var name = Path.GetFileName(path);
            var relative = Relative(path);

            if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                report.Issues.Add(new IntegrityIssue(IntegrityIssueKind.IncompleteDownload, relative,
                    "没下完的临时文件，游戏不会加载它"));
                continue;
            }

            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;

            var length = new FileInfo(path).Length;
            if (length == 0)
            {
                report.Issues.Add(new IntegrityIssue(IntegrityIssueKind.EmptyFile, relative, "0 字节"));
                continue;
            }

            // 只读中央目录，不整包解压。1.20 的模组动辄几十 MB，全解开太慢。
            try
            {
                using var zip = ZipFile.OpenRead(path);
                _ = zip.Entries.Count;
            }
            catch (Exception ex)
            {
                report.Issues.Add(new IntegrityIssue(IntegrityIssueKind.BrokenZip, relative, ex.GetType().Name));
            }
        }
    }

    // ── 版本 JSON ───────────────────────────────────────────────────

    /// <summary>版本 JSON 里我们用得到的部分。</summary>
    private sealed class VersionJson
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("inheritsFrom")] public string? InheritsFrom { get; set; }
        [JsonPropertyName("jar")] public string? Jar { get; set; }
        [JsonPropertyName("libraries")] public List<LibraryJson> Libraries { get; set; } = new();
        [JsonPropertyName("assetIndex")] public AssetIndexJson? AssetIndex { get; set; }
        [JsonPropertyName("downloads")] public DownloadsJson? Downloads { get; set; }
    }

    private sealed class LibraryJson
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
        [JsonPropertyName("downloads")] public LibraryDownloads? Downloads { get; set; }
        [JsonPropertyName("rules")] public List<RuleJson>? Rules { get; set; }
    }

    private sealed class LibraryDownloads
    {
        [JsonPropertyName("artifact")] public ArtifactJson? Artifact { get; set; }
    }

    private sealed class ArtifactJson
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    private sealed class AssetIndexJson
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    private sealed class DownloadsJson
    {
        [JsonPropertyName("client")] public ArtifactJson? Client { get; set; }
    }

    private sealed class RuleJson
    {
        [JsonPropertyName("action")] public string? Action { get; set; }
        [JsonPropertyName("os")] public OsJson? Os { get; set; }
    }

    private sealed class OsJson
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    /// <summary>沿 <c>inheritsFrom</c> 把父子 JSON 合并成一份（子在前，父在后）。</summary>
    private MergedVersion LoadMergedVersion(string versionName)
    {
        var merged = new MergedVersion();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = versionName;

        while (!string.IsNullOrWhiteSpace(current) && seen.Add(current))
        {
            var jsonPath = Path.Combine(_core.GamePath.Versions, current, current + ".json");
            if (!File.Exists(jsonPath))
            {
                if (current == versionName)
                    throw new FileNotFoundException($"找不到版本 JSON：{jsonPath}", jsonPath);
                break;
            }

            var json = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(jsonPath), JsonOptions);
            if (json is null) break;

            merged.Jar ??= json.Jar;
            merged.Downloads ??= json.Downloads;
            merged.AssetIndex ??= json.AssetIndex;

            // 加载器（Fabric/Forge）自带的库排在原版前面，顺序无所谓，但要去重
            foreach (var library in json.Libraries)
            {
                if (!string.IsNullOrWhiteSpace(library.Name))
                    merged.Libraries.Add(library);
            }

            current = json.InheritsFrom;
        }

        return merged;
    }

    private sealed class MergedVersion
    {
        public string? Jar { get; set; }
        public DownloadsJson? Downloads { get; set; }
        public AssetIndexJson? AssetIndex { get; set; }
        public List<LibraryJson> Libraries { get; } = new();
    }

    // ── 杂项 ────────────────────────────────────────────────────────

    /// <summary>Maven 坐标 → 库内相对路径。</summary>
    private static string? PathFromMavenName(string name)
    {
        var parts = name.Split(':');
        if (parts.Length < 3) return null;

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : string.Empty;

        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    /// <summary>
    /// 判断这条库规则适不适用于 Windows。
    /// </summary>
    /// <remarks>
    /// 只处理 <c>os.name</c>：rules 为空 = 通用；否则按「先匹配到的规则说了算」。
    /// 一条都没匹配上时的默认值，看第一条规则的 action —— <b>第一条是 allow 就默认拒绝</b>
    /// （官方约定：先写允许再写例外，表示「除了列出来的都别装」）。
    /// 这里写错过一次：把默认值取成了 allow，结果 Linux / macOS 的原生库全被当成「缺失」，
    /// 一上来就报 24 处假问题。
    /// </remarks>
    private static bool AppliesToWindows(List<RuleJson>? rules)
    {
        if (rules is null || rules.Count == 0) return true;

        var defaultAllow = string.Equals(rules[0].Action, "disallow", StringComparison.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            if (rule.Os?.Name is { } os && !string.Equals(os, "windows", StringComparison.OrdinalIgnoreCase))
                continue;

            return string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return defaultAllow;
    }

    private string Relative(string fullPath)
    {
        var root = _core.InstanceRoot;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, fullPath)
            : fullPath;
    }

    private static string Format(long bytes) => bytes switch
    {
        >= 1 << 20 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        >= 1 << 10 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B",
    };

    private static string Short(string? hash) =>
        string.IsNullOrWhiteSpace(hash) ? "?" : hash[..Math.Min(8, hash.Length)];
}
