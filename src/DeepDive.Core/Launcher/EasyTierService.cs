using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepDive.Core.Launcher;

/// <summary>联机状态。</summary>
public enum EasyTierState
{
    /// <summary>没有在联机。</summary>
    Idle,

    /// <summary>正在启动 core（提权弹窗可能在等用户点「是」）。</summary>
    Starting,

    /// <summary>已建好虚拟网卡、分到虚拟 IP，可以正常联机。</summary>
    Online,

    /// <summary>启动失败（没点 UAC / 建网卡失败 / core 崩了）。</summary>
    Failed,

    /// <summary>正在停止。</summary>
    Stopping,
}

/// <summary>一个联机节点。</summary>
/// <param name="Ipv4">虚拟 IP，如 <c>10.20.30.1/24</c>。</param>
/// <param name="Hostname">主机名。</param>
/// <param name="LatencyMs">延迟（毫秒），<c>null</c> 表示还没测到。</param>
/// <param name="IsSelf">是不是本机。</param>
public sealed record EasyTierPeer(string? Ipv4, string Hostname, double? LatencyMs, bool IsSelf);

/// <summary>一次联机会话的状态快照，界面直接绑它。</summary>
public sealed class EasyTierSession
{
    public EasyTierState State { get; set; } = EasyTierState.Idle;

    /// <summary>本机分到的虚拟 IP，如 <c>10.20.30.1/24</c>。</summary>
    public string? VirtualIp { get; set; }

    /// <summary>房间码（网络名）。联机时才有。</summary>
    public string? NetworkName { get; set; }

    /// <summary>房间密码（网络密钥）。</summary>
    public string? NetworkSecret { get; set; }

    /// <summary>当前在线的节点。</summary>
    public IReadOnlyList<EasyTierPeer> Peers { get; set; } = Array.Empty<EasyTierPeer>();

    /// <summary>给用户看的一行状态说明。</summary>
    public string StatusText { get; set; } = string.Empty;

    /// <summary>是否处于「已联机」状态。</summary>
    public bool IsOnline => State == EasyTierState.Online;
}

/// <summary>
/// EasyTier 联机：把 <c>easytier-core.exe</c> 作为子进程拉起，建虚拟局域网。
/// </summary>
/// <remarks>
/// <para>
/// <b>权限模型（必须理解，否则会踩坑）：</b>
/// EasyTier 建虚拟网卡（Wintun TUN）需要管理员权限。所以「启动」这个动作用
/// <c>ProcessStartInfo.Verb = "runas"</c> 触发 UAC，让 core 在提权进程里跑。
/// 但<b>读状态不需要提权</b>：core 起来后会开一个 RPC 端口（默认 <c>127.0.0.1:15889</c>），
/// 普通进程通过 <c>easytier-cli.exe</c> 或直接打它的 HTTP 接口就能查到节点。
/// 所以架构是：提权只发生一次（建网那一刻），之后所有交互都在普通进程里做。
/// </para>
/// <para>
/// <b>为什么用 core 而不是自己实现：</b>EasyTier 是 LGPL-3.0，用 <c>Process.Start</c>
/// 调它的 exe 属于进程间调用，不传染我们的代码。但分发时要附 LGPL 副本、关于页声明、
/// 允许用户替换这个组件 —— 这些在 App 层处理。
/// </para>
/// <para>
/// <b>房间码设计：</b>网络名 = 房间码（短、好念），网络密钥 = 密码。
/// 房主点「创建」生成一对；加入者输入这对。EasyTier 的 <c>network-name</c> +
/// <c>network-secret</c> 就天然是这个模型，不需要自己再套一层。
/// </para>
/// </remarks>
public sealed class EasyTierService : IDisposable
{
    /// <summary>RPC 端口。固定一个，避免每次随机导致「停不掉旧实例」。</summary>
    public const int RpcPort = 15889;

    /// <summary>监听端口。tcp 监听，给对端 NAT 打洞用。</summary>
    public const int ListenPort = 11011;

    /// <summary>虚拟网段。房主用 .1，加入者会自动在段内分配。</summary>
    private const string VpnSubnet = "10.20.30.0/24";

    private const string HostVpnIp = "10.20.30.1/24";

    private readonly string _binaryDir;
    private Process? _coreProcess;
    private CancellationTokenSource? _watchCts;

    public EasyTierSession Session { get; } = new();

    public Action<string>? Log { get; set; }

    public EasyTierService(string binaryDir)
    {
        _binaryDir = binaryDir;
    }

    /// <summary>core 可执行文件的路径。</summary>
    private string CorePath => Path.Combine(_binaryDir, "easytier-core.exe");

    /// <summary>cli 可执行文件的路径。</summary>
    private string CliPath => Path.Combine(_binaryDir, "easytier-cli.exe");

    /// <summary>内置的 EasyTier 组件是不是都在。</summary>
    public bool IsAvailable =>
        File.Exists(CorePath) && File.Exists(CliPath);

    /// <summary>生成一个房间码，形如 <c>MC-7K3F</c>。</summary>
    /// <remarks>避开 0/O、1/I/L 这些易混字符，纯大写字母 + 数字。</remarks>
    public static string GenerateNetworkName()
    {
        const string chars = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
        var sb = new StringBuilder(6);
        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        var bytes = new byte[6];
        rng.GetBytes(bytes);

        for (var i = 0; i < 6; i++)
            sb.Append(chars[bytes[i] % chars.Length]);

        return "MC-" + sb;
    }

    /// <summary>生成一个 4 位数字房间密码。</summary>
    public static string GenerateNetworkSecret()
    {
        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        var bytes = new byte[4];
        rng.GetBytes(bytes);
        return (BitConverter.ToUInt32(bytes, 0) % 10000).ToString("D4");
    }

    /// <summary>
    /// 创建房间：本机作为房主，虚拟 IP 固定 <c>10.20.30.1</c>。
    /// </summary>
    /// <param name="networkName">房间码（网络名）。传 <c>null</c> 自动生成。</param>
    /// <param name="networkSecret">房间密码。传 <c>null</c> 自动生成。</param>
    public async Task<(bool Ok, string? NetworkName, string? NetworkSecret, string? Error)>
        CreateAsync(string? networkName = null, string? networkSecret = null)
    {
        var name = string.IsNullOrWhiteSpace(networkName) ? GenerateNetworkName() : networkName.Trim();
        var secret = string.IsNullOrWhiteSpace(networkSecret) ? GenerateNetworkSecret() : networkSecret.Trim();

        var started = await StartCoreAsync(name, secret, isHost: true).ConfigureAwait(false);

        if (!started.Ok)
        {
            return (false, name, secret, Session.StatusText);
        }

        return (true, name, secret, null);
    }

    /// <summary>
    /// 加入房间：以房主给的房间码 + 密码入网，虚拟 IP 由 EasyTier 自动分配。
    /// </summary>
    public async Task<(bool Ok, string? Error)> JoinAsync(string networkName, string networkSecret)
    {
        return await StartCoreAsync(networkName.Trim(), networkSecret.Trim(), isHost: false)
            .ConfigureAwait(false);
    }

    /// <summary>把 core 拉起来。房主和加入者只是虚拟 IP 处理不同。</summary>
    private async Task<(bool Ok, string? Error)> StartCoreAsync(
        string networkName, string networkSecret, bool isHost)
    {
        if (!IsAvailable)
        {
            Session.State = EasyTierState.Failed;
            Session.StatusText = "找不到 EasyTier 组件（easytier-core.exe），需要重新下载。";
            return (false, Session.StatusText);
        }

        Session.State = EasyTierState.Starting;
        Session.NetworkName = networkName;
        Session.NetworkSecret = networkSecret;
        Session.StatusText = "正在请求管理员权限…";

        // 组装参数：网络身份 + 监听 + RPC + UDP 广播中继（MC 局域网发现靠它）
        var args = new List<string>
        {
            "--network-name", networkName,
            "--network-secret", networkSecret,
            "--rpc-portal", $"127.0.0.1:{RpcPort}",
            "--listeners", $"tcp://0.0.0.0:{ListenPort}",
            "--enable-udp-broadcast-relay", "true",
            "--console-log-level", "warn",
        };

        // 房主固定 .1；加入者不指定 IP，让 EasyTier 在段内自动分
        if (isHost)
        {
            args.Add("--ipv4");
            args.Add(HostVpnIp);
        }
        else
        {
            args.Add("--dhcp");
        }

        var psi = new ProcessStartInfo
        {
            FileName = CorePath,
            WorkingDirectory = _binaryDir,
            UseShellExecute = true,
            Verb = "runas",          // 弹 UAC，建网卡需要管理员
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        Log?.Invoke($"[联机] 拉起 core：{string.Join(" ", args)}");

        try
        {
            _coreProcess = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 = 用户在 UAC 弹窗点了「否」
            var canceled = ex.NativeErrorCode == 1223;

            Session.State = EasyTierState.Failed;
            Session.StatusText = canceled
                ? "你在管理员授权弹窗里点了「否」。要联机得先同意授权。"
                : $"启动失败：{ex.Message}";

            return (false, Session.StatusText);
        }
        catch (Exception ex)
        {
            Session.State = EasyTierState.Failed;
            Session.StatusText = $"启动失败：{ex.Message}";
            return (false, Session.StatusText);
        }

        // core 启动后需要一点时间建网卡、分 IP。轮询 RPC，最多等 20 秒。
        Log?.Invoke("[联机] core 已拉起，等待虚拟网卡就绪…");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(800).ConfigureAwait(false);

            var node = await QueryNodeAsync().ConfigureAwait(false);

            if (node is { Ip: not null } n && !string.IsNullOrWhiteSpace(n.Ip))
            {
                Session.State = EasyTierState.Online;
                Session.VirtualIp = n.Ip;
                Session.StatusText = $"已联机，你的虚拟 IP：{n.Ip}";

                // 启动后台监听，定时刷新节点列表
                StartPeerWatcher();

                return (true, null);
            }

            // core 可能已经退出了（建网卡失败）
            if (_coreProcess is { HasExited: true })
            {
                Session.State = EasyTierState.Failed;
                Session.StatusText = "core 启动后退出了。多半是建虚拟网卡失败，看看是不是杀软拦了 wintun.dll。";
                return (false, Session.StatusText);
            }
        }

        Session.State = EasyTierState.Failed;
        Session.StatusText = "等了 20 秒虚拟网卡还没就绪。检查是不是有别的 EasyTier 实例在跑。";
        return (false, Session.StatusText);
    }

    /// <summary>通过 RPC 查本节点状态。查不到返回 <c>null</c>。</summary>
    private async Task<(string? Ip, string? Hostname)?> QueryNodeAsync()
    {
        var output = await RunCliAsync("node").ConfigureAwait(false);
        if (output is null) return null;

        // cli node 输出是表格：| Virtual IP | 10.20.30.1/24 |  ...
        var ip = ExtractCell(output, "Virtual IP");
        var host = ExtractCell(output, "Hostname");

        if (string.IsNullOrWhiteSpace(ip) && string.IsNullOrWhiteSpace(host))
            return null;

        return (ip, host);
    }

    /// <summary>查所有在线节点。</summary>
    private async Task<IReadOnlyList<EasyTierPeer>> QueryPeersAsync()
    {
        var output = await RunCliAsync("peer").ConfigureAwait(false);
        if (output is null) return Array.Empty<EasyTierPeer>();

        var list = new List<EasyTierPeer>();

        // peer 表格每行：| 10.20.30.1/24 | PC-xxx | ... | lat(ms) | ...
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains('|')) continue;

            var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (cells.Length < 2) continue;

            var ipv4 = cells[0];
            var hostname = cells[1];

            // 表头行和分隔行跳过
            if (hostname is "hostname" || hostname.StartsWith("--"))
                continue;

            // 延迟在表里位置不定，用正则从整行抓
            double? latency = null;
            var m = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*(?:ms)?\s*\|");
            if (m.Success && double.TryParse(m.Groups[1].Value, out var l))
                latency = l;

            var isSelf = ipv4.Contains(Session.VirtualIp ?? "___no_match___", StringComparison.Ordinal);

            list.Add(new EasyTierPeer(ipv4, hostname, latency, isSelf));
        }

        return list;
    }

    /// <summary>跑一次 cli 命令，返回标准输出。失败返回 <c>null</c>。</summary>
    private async Task<string?> RunCliAsync(string subcommand)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = CliPath,
                WorkingDirectory = _binaryDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add($"127.0.0.1:{RpcPort}");
            psi.ArgumentList.Add(subcommand);

            using var process = Process.Start(psi);
            if (process is null) return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var exited = await Task.Run(() => process.WaitForExit(4000)).ConfigureAwait(false);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            return stdout;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>后台定时刷新节点列表。</summary>
    private void StartPeerWatcher()
    {
        _watchCts?.Cancel();
        _watchCts = new CancellationTokenSource();
        var token = _watchCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && Session.IsOnline)
            {
                try
                {
                    var peers = await QueryPeersAsync().ConfigureAwait(false);
                    Session.Peers = peers;
                }
                catch
                {
                    // 单次刷新失败不影响下次
                }

                await Task.Delay(3000, token).ConfigureAwait(false);
            }
        }, token);
    }

    /// <summary>停止联机：让 core 退出，网卡会自动移除。</summary>
    public void Stop()
    {
        _watchCts?.Cancel();
        Session.State = EasyTierState.Stopping;
        Session.StatusText = "正在停止…";

        try
        {
            // 通过 cli 优雅退出；失败就直接杀进程
            var psi = new ProcessStartInfo
            {
                FileName = CliPath,
                WorkingDirectory = _binaryDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add($"127.0.0.1:{RpcPort}");
            psi.ArgumentList.Add("service");
            psi.ArgumentList.Add("stop");

            using var process = Process.Start(psi);
            if (process is not null)
            {
                process.WaitForExit(4000);
            }
        }
        catch
        {
            // cli 停不掉就退回杀进程
        }

        TryKillCore();

        Session.State = EasyTierState.Idle;
        Session.VirtualIp = null;
        Session.NetworkName = null;
        Session.NetworkSecret = null;
        Session.Peers = Array.Empty<EasyTierPeer>();
        Session.StatusText = "未联机";
        _coreProcess = null;

        Log?.Invoke("[联机] 已停止");
    }

    private void TryKillCore()
    {
        try
        {
            if (_coreProcess is { HasExited: false })
                _coreProcess.Kill(entireProcessTree: true);
        }
        catch { }

        // 兜底：按进程名再扫一遍，防止 core 由 runas 拉起的子进程逃逸
        try
        {
            foreach (var p in Process.GetProcessesByName("easytier-core"))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.Dispose();
            }
        }
        catch { }
    }

    /// <summary>从 cli 的表格输出里取某一行的值。</summary>
    private static string? ExtractCell(string output, string key)
    {
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains(key, StringComparison.OrdinalIgnoreCase)) continue;

            var idx = line.IndexOf('|');
            if (idx < 0) continue;

            // 形如 "| Virtual IP | 10.20.30.1/24 |"：取第二个 | 后面的值
            var rest = line[(idx + 1)..];
            var idx2 = rest.IndexOf('|');
            if (idx2 < 0) continue;

            var value = rest[(idx2 + 1)..];
            var end = value.IndexOf('|');
            if (end >= 0) value = value[..end];

            value = value.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    public void Dispose()
    {
        _watchCts?.Cancel();
        TryKillCore();
        _watchCts?.Dispose();
    }
}
