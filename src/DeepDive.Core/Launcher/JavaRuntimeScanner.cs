using System.Diagnostics;
using System.Text;

namespace DeepDive.Core.Launcher;

/// <summary>一个 Java 运行时从哪来的 —— 界面要按这个分组。</summary>
public enum JavaOrigin
{
    /// <summary>启动器自己下到 <c>runtime\</c> 下的（随游戏版本自动装）。</summary>
    Bundled,

    /// <summary>系统里的：JAVA_HOME、PATH、Program Files 下的 JDK。</summary>
    System,

    /// <summary>用户手动指定的 javaw.exe。</summary>
    Custom,
}

/// <summary>
/// 扫到的一个 Java 运行时。
/// </summary>
/// <param name="Path"><c>javaw.exe</c> 的绝对路径。</param>
/// <param name="Version">版本号原文，如 <c>21.0.7</c> / <c>1.8.0_51</c>；读不出来是空串。</param>
/// <param name="Major">主版本号，8 / 17 / 21 / 25；读不出来是 0。</param>
/// <param name="Is64Bit">是否 64 位。32 位 Java 的堆上限只有 1024MB，内存滑块要跟着压。</param>
/// <param name="Origin">来源分类。</param>
/// <param name="Label">给人看的名字，如「21.0.7（启动器自带）」。</param>
public sealed record JavaRuntimeInfo(
    string Path,
    string Version,
    int Major,
    bool Is64Bit,
    JavaOrigin Origin,
    string Label);

/// <summary>
/// 找出这台机器上能用的 Java。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这个</b>：启动时 <c>MLaunchOption.JavaPath</c> 留空，CmlLib 会自己按版本 JSON 的
/// <c>javaVersion.component</c> 去 <c>runtime\&lt;平台&gt;\&lt;组件&gt;</c> 找。这套逻辑能用，但是个黑盒 ——
/// 用户看不到「现在到底用的哪个 Java」、装了几个、也没法换。而这个启动器在弱机器上最常见的问题
/// 恰恰是「Java 版本不对」和「32 位 Java 装不下模组包」。
/// </para>
/// <para>
/// <b>版本号从 <c>release</c> 文件读，不起进程</b>：JDK/JRE 根目录下那个 <c>release</c> 文件里就有
/// <c>JAVA_VERSION=</c> 和 <c>OS_ARCH=</c>。起进程跑 <c>java -version</c> 每条要 100~300ms，
/// 扫十几个目录就是好几秒，全压在主线程上界面直接卡住。
/// </para>
/// </remarks>
public static class JavaRuntimeScanner
{
    /// <summary>32 位 Java 的堆上限。超过这个数 JVM 直接起不来。</summary>
    public const int MaxHeapFor32Bit = 1024;

    /// <summary>Windows 下可能装 JDK 的地方。</summary>
    private static readonly string[] CommonRoots =
    {
        @"Java",
        @"Eclipse Adoptium",
        @"Eclipse Foundation",
        @"Microsoft",
        @"Amazon Corretto",
        @"Zulu",
        @"BellSoft",
        @"Semeru",
        @"RedHat",
    };

    /// <summary>
    /// 扫一遍。结果按「主版本号从新到旧」排，同版本时自带的排前面。
    /// </summary>
    /// <param name="sharedRepository">共用库根（<c>runtime</c> 在它下面）。</param>
    public static IReadOnlyList<JavaRuntimeInfo> Scan(string sharedRepository)
    {
        var found = new List<JavaRuntimeInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string exePath, JavaOrigin origin)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return;

            string full;
            try
            {
                full = System.IO.Path.GetFullPath(exePath);
            }
            catch
            {
                return;
            }

            if (!File.Exists(full)) return;
            if (!seen.Add(full)) return;

            found.Add(Read(full, origin));
        }

        foreach (var exe in EnumerateBundled(sharedRepository)) Add(exe, JavaOrigin.Bundled);
        foreach (var exe in EnumerateSystem()) Add(exe, JavaOrigin.System);

        return found
            .OrderByDescending(j => j.Major)
            .ThenBy(j => j.Origin)
            .ToList();
    }

    /// <summary>只读一个（用户手动选的 javaw.exe）。文件不存在或不是 Java 也照样返回，靠 <see cref="JavaRuntimeInfo.Major"/> 判断。</summary>
    public static JavaRuntimeInfo Describe(string javaExePath, JavaOrigin origin = JavaOrigin.Custom)
        => Read(javaExePath, origin);

    /// <summary><c>runtime\&lt;平台&gt;\&lt;组件&gt;\bin\javaw.exe</c>，CmlLib 下到这儿。</summary>
    private static IEnumerable<string> EnumerateBundled(string sharedRepository)
    {
        if (string.IsNullOrWhiteSpace(sharedRepository)) yield break;

        var runtimeRoot = System.IO.Path.Combine(sharedRepository, "runtime");
        if (!Directory.Exists(runtimeRoot)) yield break;

        string[] platforms;
        try
        {
            platforms = Directory.GetDirectories(runtimeRoot);
        }
        catch
        {
            yield break;
        }

        foreach (var platform in platforms)
        {
            string[] components;
            try
            {
                components = Directory.GetDirectories(platform);
            }
            catch
            {
                continue;
            }

            foreach (var component in components)
            {
                foreach (var exe in CandidateExes(component))
                    yield return exe;
            }
        }
    }

    /// <summary>JAVA_HOME、PATH、Program Files 下的各家 JDK。</summary>
    private static IEnumerable<string> EnumerateSystem()
    {
        // 1) JAVA_HOME —— 最明确的一个
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            foreach (var exe in CandidateExes(javaHome))
                yield return exe;
        }

        // 2) PATH —— 用户在命令行敲 java 能跑，那个就算
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var exe = System.IO.Path.Combine(dir.Trim(), "javaw.exe");
            if (File.Exists(exe)) yield return exe;
        }

        // 3) Program Files 下几家常见发行版
        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrWhiteSpace(programFiles)) continue;

            foreach (var vendor in CommonRoots)
            {
                var vendorRoot = System.IO.Path.Combine(programFiles, vendor);
                if (!Directory.Exists(vendorRoot)) continue;

                string[] children;
                try
                {
                    children = Directory.GetDirectories(vendorRoot);
                }
                catch
                {
                    continue;
                }

                foreach (var child in children)
                    foreach (var exe in CandidateExes(child))
                        yield return exe;
            }
        }
    }

    /// <summary>一个 Java 根目录下的 javaw.exe / java.exe（优先 javaw，没有控制台窗口）。</summary>
    private static IEnumerable<string> CandidateExes(string javaHome)
    {
        var bin = System.IO.Path.Combine(javaHome, "bin");

        var javaw = System.IO.Path.Combine(bin, "javaw.exe");
        if (File.Exists(javaw))
        {
            yield return javaw;
            yield break;
        }

        var java = System.IO.Path.Combine(bin, "java.exe");
        if (File.Exists(java)) yield return java;
    }

    /// <summary>读 <c>release</c> 文件补齐版本和位数。读不到就用目录名猜，猜不出标未知。</summary>
    private static JavaRuntimeInfo Read(string exePath, JavaOrigin origin)
    {
        // 从 ...\bin\javaw.exe 往上退两级就是 Java 根目录
        var bin = System.IO.Path.GetDirectoryName(exePath) ?? string.Empty;
        var home = System.IO.Path.GetDirectoryName(bin) ?? string.Empty;

        var version = string.Empty;
        var arch = string.Empty;

        var releaseFile = System.IO.Path.Combine(home, "release");
        if (File.Exists(releaseFile))
        {
            try
            {
                foreach (var line in File.ReadLines(releaseFile))
                {
                    if (line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
                        version = Unquote(line["JAVA_VERSION=".Length..]);
                    else if (line.StartsWith("OS_ARCH=", StringComparison.Ordinal))
                        arch = Unquote(line["OS_ARCH=".Length..]);
                    else if (line.StartsWith("JAVA_ARCH=", StringComparison.Ordinal) && arch.Length == 0)
                        arch = Unquote(line["JAVA_ARCH=".Length..]);
                }
            }
            catch
            {
                // 读不动就当没有，下面靠目录名兜
            }
        }

        // release 里的版本够用了；真没有才起进程问一次（慢，但只在这一个身上发生）
        if (version.Length == 0)
            version = AskVersionByProcess(exePath);

        var major = ParseMajor(version);
        var is64 = ResolveIs64(arch, home, exePath);

        return new JavaRuntimeInfo(exePath, version, major, is64, origin, BuildLabel(version, major, is64, origin));
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }

    /// <summary>版本号 → 主版本。1.8.0_51 → 8；21.0.7 → 21。</summary>
    private static int ParseMajor(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;

        var parts = version.Split('.', '_', '-', '+');
        if (parts.Length >= 2 && parts[0] == "1" && int.TryParse(parts[1], out var legacy))
            return legacy;

        return int.TryParse(parts[0], out var major) ? major : 0;
    }

    /// <summary>
    /// 位数：先看 <c>OS_ARCH</c>，再看路径里的 <c>x64</c> / <c>x86</c>。
    /// 都判断不出来时按 64 位算 —— 现在几乎没有 32 位 Java 了，
    /// 误判成 32 位会把内存滑块白白压到 1GB。
    /// </summary>
    private static bool ResolveIs64(string arch, string javaHome, string exePath)
    {
        if (arch.Equals("x86_64", StringComparison.OrdinalIgnoreCase) ||
            arch.Equals("amd64", StringComparison.OrdinalIgnoreCase) ||
            arch.Equals("aarch64", StringComparison.OrdinalIgnoreCase))
            return true;

        if (arch.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
            arch.Equals("i386", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var probe in new[] { javaHome, exePath })
        {
            if (probe.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
                probe.Contains("64-bit", StringComparison.OrdinalIgnoreCase))
                return true;

            if (probe.Contains("x86", StringComparison.OrdinalIgnoreCase) &&
                !probe.Contains("x64", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string BuildLabel(string version, int major, bool is64, JavaOrigin origin)
    {
        var shown = version.Length > 0 ? version : (major > 0 ? major.ToString() : "版本未知");

        var originText = origin switch
        {
            JavaOrigin.Bundled => "启动器自带",
            JavaOrigin.System => "系统",
            _ => "手动指定",
        };

        return $"{shown} · {(is64 ? "64 位" : "32 位")} · {originText}";
    }

    /// <summary>
    /// 起进程问 <c>java.exe -version</c>。只在 <c>release</c> 读不到时用，慢但兜底。
    /// </summary>
    private static string AskVersionByProcess(string exePath)
    {
        try
        {
            // javaw 不输出到控制台，得换成同目录的 java.exe
            var javaExe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exePath) ?? string.Empty, "java.exe");
            var target = File.Exists(javaExe) ? javaExe : exePath;
            if (!File.Exists(target)) return string.Empty;

            var info = new ProcessStartInfo(target, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(info);
            if (process is null) return string.Empty;

            var text = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(2000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return string.Empty;
            }

            // 形如：openjdk version "21.0.7" 2025-04-15
            var match = System.Text.RegularExpressions.Regex.Match(text, "\"([0-9][^\"]*)\"");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
