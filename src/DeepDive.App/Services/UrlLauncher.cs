using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;
using DeepDive.App.Diagnostics;

namespace DeepDive.App.Services;

/// <summary>打开网页的结果。<paramref name="Verified"/> 为 true 表示真的查过浏览器进程。</summary>
public readonly record struct UrlOpenResult(bool Success, string Channel, bool Verified);

/// <summary>
/// 用默认浏览器打开一个网址，并且<b>确认它真的起来了</b>。
/// </summary>
/// <remarks>
/// <para>
/// 这条路上踩了三个坑，全记在这儿：
/// </para>
/// <para>
/// <b>坑一：<c>Process.Start(url)</c>（ShellExecute）会静默失败。</b>
/// 不抛异常、还返回进程对象，浏览器根本没出来。日志写着「已打开」，屏幕上一动不动。
/// </para>
/// <para>
/// <b>坑二：直接 CreateProcess 浏览器 exe 也会「成功」。</b>
/// 进程确实创建出来了，但立刻退出。低完整性令牌下尤其如此 ——
/// 浏览器拿不到自己的用户数据目录（<c>%LOCALAPPDATA%\Microsoft\Edge\User Data</c>
/// 是中完整性对象，低完整性进程只读不写），于是：
/// <code>Lock file can not be created! Error code: 5</code>
/// <code>Failed to create a ProcessSingleton for your profile directory.</code>
/// 然后自己 abort。<c>Process.Start</c> 照样返回 true。
/// </para>
/// <para>
/// <b>坑三（解法）：<c>--user-data-dir</c> 指到一个能写的目录，浏览器就活了。</b>
/// 实测同一个低完整性进程，带这个参数起来之后进程稳定存活、页面正常。
/// 代价是这套配置是干净的（没书签没登录），所以只在必要时才用。
/// </para>
/// <para>
/// 所以现在的顺序：低完整性 → 直接用备用配置目录；正常 → 用用户自己的配置。
/// 每条通道起完都<b>数一下浏览器进程</b>，数不到就老实说没打开，绝不再假报成功。
/// </para>
/// </remarks>
public static class UrlLauncher
{
    /// <summary>每条通道最多等多久（毫秒）。</summary>
    private const int WaitPerChannel = 900;

    /// <summary>轮询间隔。</summary>
    private const int PollInterval = 120;

    /// <summary>
    /// 试着打开 <paramref name="url"/>。
    /// </summary>
    /// <param name="url">要打开的网址。</param>
    /// <param name="profileRoot">
    /// 备用的浏览器配置目录（低完整性下必须给，否则浏览器起不来）。
    /// 传 <c>null</c> 就只走正常通道。
    /// </param>
    public static async Task<UrlOpenResult> TryOpenAsync(string url, string? profileRoot = null)
    {
        var notes = new List<string>();

        var browser = ResolveDefaultBrowser();
        if (browser.ExePath is null)
        {
            notes.Add("注册表里没读到默认浏览器的 open 命令");
        }

        var sandboxed = TokenIntegrity.IsSandboxed;
        var canUseAltProfile = profileRoot is not null
                               && browser.ExePath is not null
                               && browser.ProcessName is not null
                               && IsChromium(browser.ProcessName);

        // ① 低完整性沙箱里，浏览器自己的配置目录根本写不进去 —— 别浪费那 900ms
        if (sandboxed && canUseAltProfile)
        {
            if (await TryStartWithAltProfileAsync(browser.ExePath!, browser.ProcessName!, profileRoot!, url))
            {
                return new UrlOpenResult(true, $"{Path.GetFileName(browser.ExePath)}（临时配置，沙箱环境）", true);
            }

            notes.Add("给浏览器换配置目录也没起来");
        }

        // ② 直接起浏览器 exe，用用户自己的配置（最可控，能拿到真实成败）
        if (browser.ExePath is not null && browser.ProcessName is { } browserName)
        {
            // 千万别照抄注册表里的 --single-argument 模板。
            // 那个开关是给 ShellExecute 用的，会把引号一起当成网址内容，
            // 变成 `https://www.chunkbase.com/apps/seed-map"` 这种畸形地址 ——
            // 浏览器解析不出来，报的却是 "DNS_PROBE_FINISHED_NXDOMAIN"，
            // 看着像 DNS 故障，实际是网址尾巴上多了个引号。
            if (TryStart(browser.ExePath, Quote(url), out var startError))
            {
                if (await WaitForBrowserAsync(browserName))
                {
                    return new UrlOpenResult(true, Path.GetFileName(browser.ExePath), true);
                }

                notes.Add($"{Path.GetFileName(browser.ExePath)} 起来了但立刻退出");
            }
            else
            {
                notes.Add($"{Path.GetFileName(browser.ExePath)} 起不来：{startError}");
            }
        }

        // ③ 交给 explorer 转交（浏览器不认命令行参数时的退路）
        if (browser.ProcessName is not null)
        {
            if (TryStart("explorer.exe", $"\"{url}\"", out var explorerError))
            {
                if (await WaitForBrowserAsync(browser.ProcessName))
                {
                    return new UrlOpenResult(true, "explorer 转交", true);
                }

                notes.Add("explorer 转交后浏览器仍没起来");
            }
            else
            {
                notes.Add($"explorer 转交失败：{explorerError}");
            }
        }

        // ④ 最后才用 ShellExecute（这台机器上它常年静默失效，所以排后面）
        try
        {
            using var _ = Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

            if (browser.ProcessName is null || await WaitForBrowserAsync(browser.ProcessName))
            {
                return new UrlOpenResult(true, "ShellExecute", browser.ProcessName is not null);
            }

            notes.Add("ShellExecute 也没把浏览器拉起来");
        }
        catch (Exception ex)
        {
            notes.Add($"ShellExecute 失败：{ex.GetType().Name}: {ex.Message}");
        }

        // ⑤ 前面都没成，还没用过备用配置的话再试最后一次
        if (!sandboxed && canUseAltProfile)
        {
            if (await TryStartWithAltProfileAsync(browser.ExePath!, browser.ProcessName!, profileRoot!, url))
            {
                return new UrlOpenResult(true, $"{Path.GetFileName(browser.ExePath)}（临时配置）", true);
            }

            notes.Add("换配置目录也没起来");
        }

        return new UrlOpenResult(false, string.Join("；", notes), true);
    }

    /// <summary>用一个能写的配置目录启动 Chromium 系浏览器。</summary>
    /// <remarks>
    /// 拉上 <c>--disable-gpu</c> 是被逼的：沙箱里 GPU 进程起不来，
    /// 窗口能开、网页却是一片白（headless 加了这个参数就能正常抓取页面，正好对照）。
    /// Chunkbase 那张地图是 canvas 2D，走软件渲染一样能画，只是慢一点。
    /// </remarks>
    private static async Task<bool> TryStartWithAltProfileAsync(
        string exe, string processName, string profileRoot, string url)
    {
        string profileDir;
        try
        {
            profileDir = Path.Combine(profileRoot, "profile");
            Directory.CreateDirectory(profileDir);
        }
        catch
        {
            return false;
        }

        var arguments = $"--user-data-dir=\"{profileDir}\" --disable-gpu --no-first-run " +
                        $"--no-default-browser-check \"{url}\"";

        return TryStart(exe, arguments, out _) && await WaitForBrowserAsync(processName);
    }

    /// <summary>是不是 Chromium 系（只有它们认 <c>--user-data-dir</c>）。</summary>
    private static bool IsChromium(string processName)
        => processName is "msedge" or "chrome" or "brave" or "vivaldi" or "opera";

    /// <summary>轮询等浏览器进程出现。</summary>
    private static async Task<bool> WaitForBrowserAsync(string processName)
    {
        var waited = 0;

        while (waited <= WaitPerChannel)
        {
            try
            {
                if (Process.GetProcessesByName(processName).Length > 0) return true;
            }
            catch
            {
                // 查进程失败就当没起来
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
            waited += PollInterval;
        }

        return false;
    }

    private static bool TryStart(string exe, string arguments, out string error)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
            });

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 给网址加引号。
    /// </summary>
    /// <remarks>
    /// 只是防止命令行被空格截断，浏览器解析时会把外层引号剥掉。
    /// 注意这和 <c>--single-argument</c> 不一样 —— 那个开关不会剥引号。
    /// </remarks>
    private static string Quote(string url) => $"\"{url}\"";

    /// <summary>从注册表问出默认浏览器：exe 路径、命令行模板、进程名。</summary>
    private static (string? ExePath, string Arguments, string? ProcessName) ResolveDefaultBrowser()
    {
        try
        {
            var progId = ReadDefaultProgId();
            if (string.IsNullOrEmpty(progId)) return (null, string.Empty, null);

            var command = ReadOpenCommand(progId!);
            if (string.IsNullOrWhiteSpace(command)) return (null, string.Empty, null);

            if (!TrySplitCommand(command!, out var exe, out var arguments))
            {
                return (null, string.Empty, null);
            }

            return (exe, arguments, Path.GetFileNameWithoutExtension(exe));
        }
        catch
        {
            return (null, string.Empty, null);
        }
    }

    private static string? ReadDefaultProgId()
    {
        // https 和 http 分别有各自的关联，优先 https
        foreach (var scheme in new[] { "https", "http" })
        {
            try
            {
                var value = Registry.GetValue(
                    $@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\{scheme}\UserChoice",
                    "ProgId", null) as string;

                if (!string.IsNullOrEmpty(value)) return value;
            }
            catch
            {
                // 读不了就试下一个 scheme
            }
        }

        return null;
    }

    private static string? ReadOpenCommand(string progId)
    {
        try
        {
            return Registry.GetValue($@"HKEY_CLASSES_ROOT\{progId}\shell\open\command", string.Empty, null) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把 <c>"C:\...\msedge.exe" --single-argument %1</c> 拆成 exe 和参数。</summary>
    private static bool TrySplitCommand(string command, out string exe, out string arguments)
    {
        exe = string.Empty;
        arguments = string.Empty;

        command = command.Trim();
        if (command.Length == 0) return false;

        if (command[0] == '"')
        {
            var closing = command.IndexOf('"', 1);
            if (closing <= 1) return false;

            exe = command[1..closing];
            arguments = closing + 1 < command.Length ? command[(closing + 1)..].Trim() : string.Empty;
            return exe.Length > 0;
        }

        var space = command.IndexOf(' ');
        if (space < 0)
        {
            exe = command;
            return true;
        }

        exe = command[..space];
        arguments = command[(space + 1)..].Trim();
        return exe.Length > 0;
    }
}
