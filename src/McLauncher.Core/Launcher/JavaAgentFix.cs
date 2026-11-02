using System.IO;
using System.Reflection;

namespace McLauncher.Core.Launcher;

/// <summary>
/// 给游戏 JVM 挂上 <c>writable-fix.jar</c> 这个 agent。
/// </summary>
/// <remarks>
/// <para>
/// 它解决的问题（2026-10-29 在阿水那台机器上实测确认）：
/// 那台 Windows 的访问检查对「写」一律返回拒绝 —— 连只给 <c>Everyone</c> 完全控制的目录、
/// 连 Java 自己刚建的文件都判不可写，而实际写入完全正常。JRE 8 / 17 / 21 / 25 表现一致，
/// 所以不是某个 Java 版本的问题。
/// </para>
/// <para>
/// 后果很具体：Fabric 启动时要把游戏 jar 重映射一遍，用的是 <c>ZipFileSystem</c>，而 zipfs
/// 的「可写」判断就是 <c>readOnly = !Files.isWritable(path)</c>（用 javap 反编译确认过）。
/// 判断一坏，zipfs 直接只读，连 <c>create=true</c> 新建的 zip 都拒绝写入，于是：
/// </para>
/// <code>
/// java.io.IOException: the jar file …\client-intermediary.jar.tmp can't be written
/// </code>
/// <para>
/// agent 只把这一个方法的判断换成 <c>File.canWrite()</c>（老的 CRT 判断，在这台机器上是对的），
/// 并且**先自检**：健康机器上一个字节都不改。挂不上也不影响游戏（返回 <c>null</c> 就不加参数）。
/// </para>
/// </remarks>
public static class JavaAgentFix
{
    private const string ResourceName = "McLauncher.Core.writable-fix.jar";
    private const string FileName = "writable-fix.jar";

    /// <summary>已经解出来的 agent 路径（同一次运行里只解一次）。</summary>
    private static string? _extracted;

    /// <summary>
    /// 拼出 <c>-javaagent:…</c> 参数。解不出来 / 资源缺失时返回 <c>null</c>（不加参数，游戏照常启动）。
    /// </summary>
    /// <param name="preferredDirectory">优先解到这个目录（一般是共用库根），没有就退回程序目录、临时目录。</param>
    public static string? BuildArgument(string? preferredDirectory = null)
    {
        var path = EnsureFile(preferredDirectory);
        return path is null ? null : $"-javaagent:{path}";
    }

    private static string? EnsureFile(string? preferredDirectory)
    {
        if (_extracted is not null && File.Exists(_extracted)) return _extracted;

        var assembly = typeof(JavaAgentFix).Assembly;
        using var resource = assembly.GetManifestResourceStream(ResourceName);
        if (resource is null)
        {
            System.Diagnostics.Debug.WriteLine($"[JavaAgentFix] 找不到嵌入资源 {ResourceName}");
            return null;
        }

        foreach (var dir in CandidateDirectories(preferredDirectory))
        {
            try
            {
                Directory.CreateDirectory(dir);
                var target = Path.Combine(dir, FileName);

                // 大小一致就当已经是同一份，不重复写（每次启动都写一遍会拖慢且可能有杀软干扰）
                if (File.Exists(target) && new FileInfo(target).Length == resource.Length)
                    return _extracted = target;

                if (resource.CanSeek) resource.Position = 0;

                var temp = target + ".tmp";
                using (var file = File.Create(temp))
                    resource.CopyTo(file);

                File.Move(temp, target, overwrite: true);
                return _extracted = target;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[JavaAgentFix] 解到 {dir} 失败：{ex.Message}");
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateDirectories(string? preferredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(preferredDirectory))
            yield return Path.Combine(preferredDirectory, "agent");

        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
            yield return baseDir;

        var temp = TempDirectory();
        if (temp is not null)
            yield return temp;
    }

    private static string? TempDirectory()
    {
        try
        {
            var temp = Path.GetTempPath();
            return string.IsNullOrWhiteSpace(temp) ? null : Path.Combine(temp, "McLauncher");
        }
        catch (Exception)
        {
            // 临时目录本身取不到就跳过（这台机器上 TEMP 的 ACL 确实可能是坏的）
            return null;
        }
    }
}
