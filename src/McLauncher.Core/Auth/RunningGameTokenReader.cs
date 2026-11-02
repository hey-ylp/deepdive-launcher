using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace McLauncher.Core.Auth;

/// <summary>从一个正在运行的游戏进程里读出来的登录信息。</summary>
/// <param name="Username">玩家名。</param>
/// <param name="Uuid">玩家 UUID（32 位无横线，和命令行里给的一样）。</param>
/// <param name="AccessToken">Java 访问令牌。</param>
/// <param name="UserType">命令行里的 <c>--userType</c>。<c>msa</c> 才是正版。</param>
/// <param name="ProcessId">进程号，日志里用得上。</param>
public sealed record RunningGameToken(
    string Username,
    string Uuid,
    string AccessToken,
    string UserType,
    int ProcessId);

/// <summary>
/// 从<b>正在运行</b>的 Minecraft 进程的命令行里读登录令牌。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这个：</b>新版 Minecraft 官方启动器（Microsoft Store 那版）
/// <b>不把 Java 令牌写到磁盘上</b>。实测它的
/// <c>launcher_accounts_microsoft_store.json</c> 里 <c>accessToken</c> 是空串，
/// 那个 DPAPI 加密的 <c>launcher_msa_credentials_*.bin</c> 解出来只有
/// <c>{"activeUserXuid": "..."}</c>，没有令牌。
/// 令牌只在启动游戏的那一刻通过命令行参数 <c>--accessToken</c> 传进去。
/// </para>
/// <para>
/// 所以只剩这一条路：<b>游戏开着的时候</b>，从进程命令行里读。
/// 这读的是操作系统的公开信息（<c>Win32_Process.CommandLine</c>），
/// 不碰任何加密数据、不需要提权、也不需要管理员。
/// </para>
/// <para>
/// <b>代价：抓到的令牌同样只有 24 小时。</b>过期后要再开一次游戏。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class RunningGameTokenReader
{
    /// <summary>
    /// 扫一遍所有 Java 进程，挑出正在跑 Minecraft 的那些。
    /// </summary>
    /// <remarks>
    /// 判据是命令行里同时有 <c>--accessToken</c> 和 <c>--username</c> —— 服务器的
    /// <c>-jar server.jar</c> 命令行没有这些，不会误判。
    /// 顺带跳过 <c>--userType legacy</c> 的：那是离线启动，令牌是编出来的，没用。
    /// </remarks>
    public static IReadOnlyList<RunningGameToken> Scan()
    {
        var found = new List<RunningGameToken>();

        IEnumerable<Process> javaProcesses;
        try
        {
            javaProcesses = Process.GetProcesses()
                .Where(p => IsJava(p))
                .ToList();
        }
        catch
        {
            return found;
        }

        // 用 WMI 批量拿命令行比逐个进程查快得多，而且某些进程读不到时不会整批失败
        var commandLines = ReadCommandLines();

        foreach (var process in javaProcesses)
        {
            try
            {
                if (!commandLines.TryGetValue(process.Id, out var cmd) || string.IsNullOrEmpty(cmd))
                    continue;

                if (!cmd.Contains("--accessToken", StringComparison.OrdinalIgnoreCase)) continue;

                var username = Arg(cmd, "username");
                var uuid = Arg(cmd, "uuid");
                var token = Arg(cmd, "accessToken");
                var userType = Arg(cmd, "userType") ?? string.Empty;

                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(token)) continue;

                // legacy = 离线启动，令牌是启动器随便编的，导进来没有任何意义
                if (userType.Equals("legacy", StringComparison.OrdinalIgnoreCase)) continue;

                found.Add(new RunningGameToken(
                    username!, uuid ?? string.Empty, token!, userType, process.Id));
            }
            catch
            {
                // 进程刚好退出 / 权限不够，跳过
            }
            finally
            {
                process.Dispose();
            }
        }

        return found;
    }

    /// <summary>一次性把所有进程的命令行读成字典。</summary>
    private static Dictionary<int, string> ReadCommandLines()
    {
        var map = new Dictionary<int, string>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process");

            foreach (var obj in searcher.Get())
            {
                try
                {
                    var pid = Convert.ToInt32(obj["ProcessId"] ?? 0);
                    var cmd = obj["CommandLine"]?.ToString();

                    if (pid > 0 && !string.IsNullOrEmpty(cmd)) map[pid] = cmd;
                }
                finally
                {
                    obj.Dispose();
                }
            }
        }
        catch
        {
            // WMI 不可用（极少数精简系统 / 策略限制）。返回空字典，调用方会说"没找到"
        }

        return map;
    }

    private static bool IsJava(Process process)
    {
        try
        {
            var name = process.ProcessName;
            return name.Equals("java", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("javaw", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从命令行里取一个 <c>--参数 值</c>。值可能带引号。</summary>
    private static string? Arg(string commandLine, string name)
    {
        var match = Regex.Match(
            commandLine,
            $@"--{Regex.Escape(name)}\s+(""(?<q>[^""]*)""|(?<bare>\S+))",
            RegexOptions.IgnoreCase);

        if (!match.Success) return null;

        return match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["bare"].Value;
    }
}
