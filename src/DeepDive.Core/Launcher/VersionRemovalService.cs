namespace DeepDive.Core.Launcher;

/// <summary>版本目录里某一类文件（模组 / 存档 / 版本本体）。</summary>
/// <param name="Label">给人看的名字。</param>
/// <param name="Path">实际路径。</param>
/// <param name="Bytes">占用字节数。</param>
/// <param name="Count">条目数（模组是个数、存档是世界数，版本本体为 0）。</param>
public sealed record RemovalItem(string Label, string Path, long Bytes, int Count)
{
    public string SizeLabel => HumanSize.Format(Bytes);
}

/// <summary>
/// 删除前的体检报告。
/// </summary>
/// <remarks>
/// 存在的理由：删版本是个不可逆操作，用户点「确定」之前必须看清楚
/// <b>会删掉几个模组、几个存档</b>，而不只是「删掉一个版本」。
/// 版本隔离开启后这些都是版本目录里的真实文件，删了就没了。
/// </remarks>
public sealed record RemovalPreview(
    string VersionName,
    string Directory,
    long TotalBytes,
    IReadOnlyList<RemovalItem> Items,
    IReadOnlyList<string> InheritedBy)
{
    /// <summary>总大小（人读）。</summary>
    public string TotalSizeLabel => HumanSize.Format(TotalBytes);

    /// <summary>是否有别的版本继承它。有的话删掉会让那些版本起不来。</summary>
    public bool HasInheritors => InheritedBy.Count > 0;

    /// <summary>被继承时的警告文案。</summary>
    public string InheritorWarning => HasInheritors
        ? $"以下 {InheritedBy.Count} 个版本继承自它，删除后它们将无法启动：{string.Join("、", InheritedBy)}"
        : string.Empty;
}

/// <summary>
/// 卸载版本（含它的模组与存档）。
/// </summary>
/// <remarks>
/// <para>
/// 只删 <c>versions/&lt;版本名&gt;/</c> 这一个目录，<b>不动</b> <c>libraries</c> 和
/// <c>assets</c> —— 那两个是整个实例共用的，删了别的版本就起不来。
/// 这也是 HMCL / PCL2 的一致做法：宁可留残留，不冒删坏的风险。
/// </para>
/// <para>
/// 版本隔离开启时 <c>mods</c>、<c>saves</c>、<c>config</c>、<c>options.txt</c> 都在这个
/// 目录里，所以删除就是「连模组带存档一起清掉」，不会波及别的版本。
/// 隔离关闭时它们位于实例根，删除版本不会碰 —— 报告里会明确写清楚。
/// </para>
/// </remarks>
public sealed class VersionRemovalService
{
    private readonly MinecraftLauncherService _core;

    public VersionRemovalService(MinecraftLauncherService core)
        => _core = core ?? throw new ArgumentNullException(nameof(core));

    /// <summary>版本目录（<c>versions/&lt;名字&gt;</c>），删除的对象就是它。</summary>
    public string GetVersionDirectory(string versionName)
        => Path.Combine(_core.GamePath.Versions, versionName);

    /// <summary>扫描版本目录，算出会删掉什么。不产生任何修改。</summary>
    public RemovalPreview Preview(string versionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionName);

        var versionDir = GetVersionDirectory(versionName);
        if (!Directory.Exists(versionDir))
            throw new DirectoryNotFoundException($"版本目录不存在：{versionDir}");

        var items = new List<RemovalItem>();

        // 隔离开启时，模组和存档就在版本目录里，单独列出来让用户看清代价
        if (_core.IsolateVersionGameDir)
        {
            var modsDir = Path.Combine(versionDir, "mods");
            if (Directory.Exists(modsDir))
            {
                var jars = SafeFiles(modsDir, "*.jar");
                items.Add(new RemovalItem("模组", modsDir, SafeSize(modsDir), jars.Length));
            }

            var savesDir = Path.Combine(versionDir, "saves");
            if (Directory.Exists(savesDir))
            {
                // 一个世界一个目录
                var worlds = SafeDirectories(savesDir).Length;
                items.Add(new RemovalItem("存档", savesDir, SafeSize(savesDir), worlds));
            }
        }

        var total = SafeSize(versionDir);
        var inheritedBy = FindInheritors(versionName);

        return new RemovalPreview(versionName, versionDir, total, items, inheritedBy);
    }

    /// <summary>执行删除。删除前会再确认一次目录存在。</summary>
    /// <exception cref="IOException">目录被占用（通常是游戏正在运行）。</exception>
    public void Remove(string versionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionName);

        var versionDir = GetVersionDirectory(versionName);
        if (!Directory.Exists(versionDir))
            throw new DirectoryNotFoundException($"版本目录不存在：{versionDir}");

        try
        {
            Directory.Delete(versionDir, recursive: true);
        }
        catch (Exception first) when (first is IOException or UnauthorizedAccessException)
        {
            // 刚装完就卸载时，占用者往往是本进程自己：CmlLib 的 launcher 不实现 IDisposable，
            // 它开的 version_manifest_v2.json 要等 GC 才关。让核心先松手再试一次。
            _core.ReleaseFileHandles();
            Thread.Sleep(120);

            try
            {
                Directory.Delete(versionDir, recursive: true);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new IOException(
                    $"没有权限删除 {versionName}。如果游戏正在运行，请先关闭游戏再试。", ex);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"{versionName} 的文件正被占用，多半是游戏还在运行。请关闭游戏后重试。", ex);
            }
        }
    }

    /// <summary>找出所有 <c>inheritsFrom</c> 指向该版本的其它版本。</summary>
    private string[] FindInheritors(string versionName)
    {
        var versionsRoot = _core.GamePath.Versions;
        if (!Directory.Exists(versionsRoot)) return [];

        var result = new List<string>();

        foreach (var dir in SafeDirectories(versionsRoot))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name) || name == versionName) continue;

            var jsonPath = Path.Combine(dir, name + ".json");
            if (!File.Exists(jsonPath)) continue;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
                if (doc.RootElement.TryGetProperty("inheritsFrom", out var inh)
                    && string.Equals(inh.GetString(), versionName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(name);
                }
            }
            catch
            {
                // JSON 坏了就不追究它继承谁
            }
        }

        return result.ToArray();
    }

    private static string[] SafeDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return []; }
    }

    private static string[] SafeFiles(string path, string pattern)
    {
        try { return Directory.GetFiles(path, pattern, SearchOption.TopDirectoryOnly); }
        catch { return []; }
    }

    /// <summary>递归算目录大小。算不出来的项跳过，不因为一个文件没权限就整个失败。</summary>
    private static long SafeSize(string path)
    {
        if (!Directory.Exists(path)) return 0;

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* 单个文件读不到就跳过 */ }
            }
        }
        catch
        {
            // 目录枚举中断，返回已经算出来的部分
        }

        return total;
    }
}

/// <summary>字节数转人读字符串。</summary>
internal static class HumanSize
{
    public static string Format(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }
}
