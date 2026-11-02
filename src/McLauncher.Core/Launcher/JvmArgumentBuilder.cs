namespace McLauncher.Core.Launcher;

/// <summary>
/// 垃圾回收器策略。
/// </summary>
/// <remarks>
/// 四档照 PCL 的 <c>LaunchAdvanceGC</c> 设计，参数值也基本照抄 —— 那是作者实测调出来的。
/// </remarks>
public enum GcMode
{
    /// <summary>按 Java 版本挑：21+ 分代 ZGC，15~20 ZGC，其余 G1GC。</summary>
    Auto = 0,

    /// <summary>保守：只有 21+ 才用分代 ZGC，其余一律 G1GC。</summary>
    PreferZgc = 1,

    /// <summary>一律 G1GC（兼容性最好）。</summary>
    G1Only = 2,

    /// <summary>不注入任何 GC 参数，交给 JVM 默认。</summary>
    Custom = 3,
}

/// <summary>
/// 拼 JVM 的 GC 参数。
/// </summary>
/// <remarks>
/// <para>
/// <b>前提已验证</b>（2026-10-31 实测）：CmlLib 4.1.0 的默认命令行里<b>没有任何 GC 参数</b>
/// （<c>-XX:+UseG1GC</c> 出现 0 次），所以这里用 <c>ExtraJvmArguments</c> 追加是安全的 ——
/// 不会出现「Multiple garbage collectors selected」。同时 <c>--add-exports</c>、
/// <c>--enable-native-access</c>、natives 路径这些必需参数也都还在。
/// </para>
/// <para>
/// <b>所以不要改用 <c>JvmArgumentOverrides</c></b>：那个会替换整组 JVM 参数，
/// 连 <c>--add-exports java.base/jdk.internal.misc=ALL-UNNAMED</c> 一起清掉，
/// 缺了它 Minecraft 1.17+ 会 <c>IllegalAccessError</c> 直接崩。
/// </para>
/// <para>
/// <b>为什么按 Java 版本分支</b>：ZGC 在 Java 15 才有、分代 ZGC 在 21 才有。
/// 把 <c>-XX:+UseZGC</c> 塞给 Java 8，JVM 报一屏「Unrecognized VM option」，
/// 用户根本看不出是自己选错了 Java。宁可保守：<b>Java 版本判断不出来就只用 G1</b>。
/// </para>
/// </remarks>
public static class JvmArgumentBuilder
{
    /// <summary>这套策略该注入哪些参数。</summary>
    /// <param name="javaMajor">实际会用的 Java 主版本，0 = 不知道。</param>
    public static IReadOnlyList<string> ArgumentsFor(GcMode mode, int javaMajor)
    {
        if (mode == GcMode.Custom) return Array.Empty<string>();

        // Java 7 及以下：G1 还是实验特性，这一堆参数大多不认识
        if (javaMajor is > 0 and < 8) return Array.Empty<string>();

        var useZgc = mode switch
        {
            GcMode.Auto => javaMajor >= 15,
            GcMode.PreferZgc => javaMajor >= 21,
            _ => false,
        };

        // ZGC 要 Windows 10 1809（build 17763）以上，更老的系统 JVM 会拒绝
        if (useZgc && !SupportsZgc()) useZgc = false;

        if (useZgc)
        {
            // ZGC 在 21 之前一直是实验特性，不加这个开关会被拒；正式版上加也无害
            var zgc = new List<string> { "-XX:+UnlockExperimentalVMOptions", "-XX:+UseZGC" };

            // Java 23 起分代 ZGC 是默认，21/22 要显式开
            if (javaMajor is 21 or 22) zgc.Add("-XX:+ZGenerational");

            return zgc;
        }

        var g1 = new List<string>
        {
            "-XX:+UseG1GC",
            "-XX:G1NewSizePercent=20",
            "-XX:G1ReservePercent=20",
            // 32M 比官方默认的 16M 更适合大堆（整合包动辄 8G）
            "-XX:G1HeapRegionSize=32M",
            "-XX:MaxGCPauseMillis=50",
        };

        // 这两个要 Java 12+，老版本上会被当成未知参数
        if (javaMajor >= 12)
        {
            g1.Add("-XX:MinHeapFreeRatio=25");
            g1.Add("-XX:MaxHeapFreeRatio=40");
        }

        if (javaMajor == 8) g1.Add("-XX:+ParallelRefProcEnabled");

        return g1;
    }

    /// <summary>
    /// 这套策略会用什么 GC —— 给界面显示，省得用户猜。
    /// </summary>
    public static string Describe(GcMode mode, int javaMajor)
    {
        if (mode == GcMode.Custom) return "JVM 默认（不注入）";

        var args = ArgumentsFor(mode, javaMajor);
        if (args.Count == 0) return "JVM 默认（Java 版本不明或过旧）";

        if (args.Contains("-XX:+UseZGC"))
            return args.Contains("-XX:+ZGenerational") ? "分代 ZGC" : "ZGC（非分代）";

        return "G1GC（已调优）";
    }

    // ───────────────── 与 CmlLib 自带参数的去重 ─────────────────

    /// <summary>匹配 GC 选择器：<c>-XX:+UseG1GC</c>、<c>-XX:-UseZGC</c> 这类。</summary>
    private static readonly System.Text.RegularExpressions.Regex GcSelectorPattern = new(
        @"\s+-XX:[+-]Use\w*GC\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>匹配 G1 专属调优参数：<c>-XX:G1NewSizePercent=20</c> 这类。</summary>
    private static readonly System.Text.RegularExpressions.Regex G1TuningPattern = new(
        @"\s+-XX:G1\w*=\S+",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>匹配实验特性开关（重复出现没有意义，我们统一放一份）。</summary>
    private static readonly System.Text.RegularExpressions.Regex UnlockExperimentalPattern = new(
        @"\s+-XX:[+-]UnlockExperimentalVMOptions\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 把命令行里的 GC 参数整理成「只有我们这一套」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须有这一步</b>（2026-10-31 实测，血的教训）：
    /// 26.3 这类新版本的启动命令里，<b>CmlLib 自己就带了</b>
    /// <c>-XX:+UseG1GC -XX:G1NewSizePercent=20 -XX:G1ReservePercent=20
    /// -XX:MaxGCPauseMillis=50 -XX:G1HeapRegionSize=16M</c>。
    /// 我们再用 <c>ExtraJvmArguments</c> 追加 <c>-XX:+UseZGC</c>，JVM 会在启动的第一毫秒退出：
    /// </para>
    /// <code>
    /// Error occurred during initialization of VM
    /// Multiple garbage collectors selected
    /// </code>
    /// <para>
    /// 而且进程退出码是 1、没有任何日志文件 —— 表现和「游戏闪退」一模一样，
    /// 极难看出来是参数冲突。
    /// </para>
    /// <para>
    /// 早先「CmlLib 默认没有 GC 参数」的结论是错的：那次只数了 <c>-XX:+UseG1GC</c>，
    /// 而它当时跑的是另一条构建路径。别再拿那个结论当依据。
    /// </para>
    /// </remarks>
    /// <param name="commandLine">CmlLib 拼好的完整命令行。</param>
    /// <param name="injected">我们自己通过 <c>ExtraJvmArguments</c> 追加的那批参数。</param>
    /// <returns>整理后的命令行。<paramref name="injected"/> 里没有 GC 参数时原样返回。</returns>
    public static string NormalizeGc(string commandLine, IReadOnlyList<string> injected)
    {
        if (string.IsNullOrEmpty(commandLine) || injected.Count == 0) return commandLine;

        // 只重排我们自己这套里的 GC 相关项；自定义参数里的其它 -D / -Xmx 不能动
        var ours = injected.Where(IsGcParameter).ToList();
        if (ours.Count == 0) return commandLine;

        // 全删 → 再追加我们的。删的时候连我们自己那份也删掉，避免出现两份
        var result = GcSelectorPattern.Replace(commandLine, string.Empty);
        result = G1TuningPattern.Replace(result, string.Empty);
        result = UnlockExperimentalPattern.Replace(result, string.Empty);

        return result.TrimEnd() + " " + string.Join(" ", ours);
    }

    /// <summary>这条参数是不是「GC 相关、可能需要去重」的。</summary>
    private static bool IsGcParameter(string argument)
        => GcSelectorPattern.IsMatch(" " + argument)
           || G1TuningPattern.IsMatch(" " + argument)
           || UnlockExperimentalPattern.IsMatch(" " + argument);

    private static bool SupportsZgc()
    {
        try
        {
            return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);
        }
        catch
        {
            return false;
        }
    }
}
