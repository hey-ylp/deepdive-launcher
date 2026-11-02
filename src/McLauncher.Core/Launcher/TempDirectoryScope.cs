namespace McLauncher.Core.Launcher;

/// <summary>
/// 把进程的临时目录临时改到「实例根\temp」，用完还原。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须自己接管临时目录：</b>Forge / NeoForge 的官方安装器是 Java 程序，
/// 运行时会把 installer.jar 和一堆中间文件丢进 <c>Path.GetTempPath()</c>，
/// 也就是 <c>C:\Users\&lt;你&gt;\AppData\Local\Temp</c>。
/// 那个目录一旦不可写（受限进程、被策略管住、ACL 被改坏），安装会跑到一半炸掉：
/// </para>
/// <code>
/// UnauthorizedAccessException: Access to the path 'C:\Users\...\Temp\ejxjjvhv.uq1' is denied.
/// </code>
/// <para>
/// 把 <c>TMP</c> / <c>TEMP</c> / <c>TMPDIR</c> 指到实例自己的目录之后，
/// 安装器的临时文件就落在我们可以控制、可以清理的地方，而且跟游戏本体在同一个盘上 ——
/// 跨盘移动大文件也不会再多一次复制。
/// </para>
/// <para>
/// <c>Environment.SetEnvironmentVariable</c> 改的是本进程的环境块，
/// 之后由本进程拉起的 java 子进程会继承它，所以 Java 安装器看到的就是新目录。
/// 还原以前的值是为了支持嵌套（外层装原版、内层装加载器）。
/// </para>
/// </remarks>
public sealed class TempDirectoryScope : IDisposable
{
    private static readonly string[] VariableNames = ["TMP", "TEMP", "TMPDIR"];

    /// <summary>临时目录里的文件超过这个年龄就顺手清掉 —— 安装器崩过一次会留下几百 MB。</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    private readonly Dictionary<string, string?> _previous = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>实际生效的临时目录。</summary>
    public string Path { get; }

    private TempDirectoryScope(string path)
    {
        Path = path;

        foreach (var name in VariableNames)
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, path);
        }
    }

    /// <summary>把临时目录切到 <c>&lt;instanceRoot&gt;\temp</c>。返回的对象 Dispose 时自动还原。</summary>
    public static TempDirectoryScope Activate(string instanceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceRoot);

        var path = System.IO.Path.Combine(instanceRoot, "temp");
        Directory.CreateDirectory(path);
        PurgeOldFiles(path);

        return new TempDirectoryScope(path);
    }

    /// <summary>清掉一天前的残留。清不掉（被占用/没权限）就算了，绝不能因此让安装失败。</summary>
    private static void PurgeOldFiles(string path)
    {
        try
        {
            var deadline = DateTime.UtcNow - MaxAge;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < deadline)
                        File.Delete(file);
                }
                catch
                {
                    // 单个文件删不掉无所谓
                }
            }
        }
        catch
        {
            // 目录枚举失败也无所谓
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (name, value) in _previous)
        {
            try
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            catch
            {
                // 进程正在退出时可能已经改不动环境变量了
            }
        }
    }
}
