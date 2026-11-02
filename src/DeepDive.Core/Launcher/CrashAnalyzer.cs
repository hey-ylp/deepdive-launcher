using System.Text;
using System.Text.RegularExpressions;

namespace DeepDive.Core.Launcher;

/// <summary>一条崩溃结论的严重程度。</summary>
public enum CrashSeverity
{
    /// <summary>游戏根本没起来 / 必崩，得处理。</summary>
    Fatal,

    /// <summary>能跑但有问题，或者只是可能的原因。</summary>
    Warning,

    /// <summary>仅供参考的信息。</summary>
    Info,
}

/// <summary>
/// 一条人话结论。
/// </summary>
/// <param name="Title">一句话结论（「Java 版本不对」）。</param>
/// <param name="Explanation">为什么这么判断（说明白，别只给结论）。</param>
/// <param name="Advice">怎么办（能照着做）。</param>
/// <param name="Evidence">日志里的原文证据，用来让人自己核对。</param>
/// <param name="Suspects">嫌疑模组的 jar 文件名（不带路径）。</param>
public sealed record CrashFinding(
    string Title,
    CrashSeverity Severity,
    string Explanation,
    string Advice,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Suspects)
{
    /// <summary>能不能「一键禁用」：有嫌疑模组且它们确实在 mods 目录里。</summary>
    public bool CanDisableSuspects => Suspects.Count > 0;
}

/// <summary>一次分析的结果。</summary>
/// <param name="SourcePath">读的是哪个文件（崩溃报告或 latest.log）。</param>
/// <param name="HasCrashReport">有没有真的找到 crash-report（false 说明只能靠 latest.log）。</param>
/// <param name="Description">崩溃报告里的 <c>Description:</c>。</param>
/// <param name="GameVersion">崩溃报告里的 <c>Minecraft Version:</c>。</param>
/// <param name="JavaVersion">崩溃报告里的 <c>Java Version:</c>。</param>
/// <param name="Findings">结论，按严重程度排好序。</param>
/// <param name="GameDir">游戏目录（禁用模组时要用）。</param>
public sealed record CrashAnalysis(
    string? SourcePath,
    bool HasCrashReport,
    string? Description,
    string? GameVersion,
    string? JavaVersion,
    IReadOnlyList<CrashFinding> Findings,
    string GameDir)
{
    /// <summary>有没有找到任何可说的东西。</summary>
    public bool HasFindings => Findings.Count > 0;

    /// <summary>最严重的那条的标题，用来做窗口标题。</summary>
    public string Headline => Findings.Count == 0
        ? "没看出明显原因"
        : Findings[0].Title;

    /// <summary>给日志区用的单行摘要。</summary>
    public string BriefSummary
    {
        get
        {
            if (Findings.Count == 0)
                return HasCrashReport ? "崩溃报告读到了，但没命中已知规则" : "没找到崩溃报告，日志里也没有报错";

            var fatal = Findings.Count(f => f.Severity == CrashSeverity.Fatal);

            if (fatal == 0 && !HasCrashReport)
                return "最近一次启动正常，没有崩溃";

            return Findings.Count == 0
                ? "崩溃报告读到了，但没命中已知规则，建议把日志发出来看"
                : $"找到 {Findings.Count} 条结论（{fatal} 条致命）：{Headline}";
        }
    }
}

/// <summary>
/// 崩溃分析：读 <c>crash-reports/*.txt</c> 和 <c>logs/latest.log</c>，按本地规则给出人话结论。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么先做规则、不接 AI</b>：模组崩溃的绝大部分是同一批错误（内存、Java 版本、
/// mixin 注入、缺前置、文件损坏），本地规则能做到「秒回 + 不花钱 + 不联网」，
/// 而且结论里能直接带上「哪个 jar 有问题」。AI 适合兜底那些规则没命中的怪问题。
/// </para>
/// <para>
/// <b>规则要能说出「证据」</b>：只给结论（「检查模组冲突」）没有用。
/// 每条结论都带上日志原文行，用户能自己核对，也能直接贴给别人问。
/// </para>
/// <para>
/// <b>嫌疑模组怎么找</b>：优先取崩溃报告里的 <c>Mod File: xxx.jar</c>（Forge / NeoForge 会写），
/// 其次从堆栈里的 <c>mods\xxx.jar</c>，最后用「报错的类名包段」去对 mods 目录里的文件名
/// （<c>lithium</c> → <c>lithium-neoforge-0.15.3+mc1.21.4.jar</c>）。
/// 宁可少报，不能乱指 —— 指错了用户会白删一个好模组。
/// </para>
/// </remarks>
public static class CrashAnalyzer
{
    /// <summary>latest.log 只读最后这么多行（前面的对定位崩溃没用）。</summary>
    private const int LogTailLines = 1500;

    /// <summary>每条结论最多带几行证据，多了窗口里放不下。</summary>
    private const int MaxEvidenceLines = 4;

    /// <summary>
    /// 分析某个游戏目录最近一次崩溃。
    /// </summary>
    /// <param name="gameDir">游戏工作目录（版本隔离时是 <c>versions/&lt;版本&gt;/</c>）。</param>
    /// <returns>没找到任何日志时返回 <c>null</c>。</returns>
    public static CrashAnalysis? Analyze(string gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return null;

        var crashReport = FindNewestCrashReport(gameDir);
        var logTail = ReadLogTail(Path.Combine(gameDir, "logs", "latest.log"));

        if (crashReport is null && logTail is null) return null;

        var reportText = crashReport is null ? string.Empty : SafeRead(crashReport) ?? string.Empty;
        var combined = reportText + "\n" + (logTail ?? string.Empty);

        var findings = RunRules(combined, reportText, logTail, gameDir);

        // 有崩溃报告但一条规则都没命中：至少给一条「去哪看」的结论，
        // 否则用户会以为分析器坏了
        if (findings.Count == 0 && crashReport is not null)
        {
            findings.Add(new CrashFinding(
                "没命中已知规则",
                CrashSeverity.Warning,
                "崩溃报告读到了，但里面的错误不在本地规则覆盖范围内（多半是某个模组独有的问题）。",
                "把 crash-reports 里最新的那个 .txt 打开，看最上面的 Description 和第一段栈顶信息；"
                + "也可以把文件内容贴到模组作者的问题页。",
                ExtractDescription(reportText) is { Length: > 0 } desc ? new[] { desc } : Array.Empty<string>(),
                Array.Empty<string>()));
        }

        // 连崩溃报告都没有、规则也没命中 = 这次启动其实是好的。
        // 但空窗口等于什么都没说（用户会以为点了没反应），至少把日志里的警告摊出来
        if (findings.Count == 0 && crashReport is null && logTail is not null)
        {
            findings.Add(BuildNormalRunFinding(logTail,
                File.Exists(Path.Combine(gameDir, "logs", "latest.log"))
                    ? File.GetLastWriteTime(Path.Combine(gameDir, "logs", "latest.log"))
                    : null));
        }

        var ordered = findings
            .OrderBy(f => f.Severity)
            .ToList();

        return new CrashAnalysis(
            crashReport ?? Path.Combine(gameDir, "logs", "latest.log"),
            crashReport is not null,
            ExtractHeader(reportText, "Description"),
            ExtractHeader(reportText, "Minecraft Version"),
            ExtractHeader(reportText, "Java Version"),
            ordered,
            gameDir);
    }

    /// <summary>
    /// 把嫌疑模组挪进 <c>mods-disabled/</c>。
    /// </summary>
    /// <remarks>
    /// 只挪不删 —— 用户随时能挪回来。这也是启动器里已有的约定
    /// （之前手动处理 visuality / sodiumdynamiclights 就是这么放的）。
    /// </remarks>
    /// <returns>真的挪走了哪些。</returns>
    public static IReadOnlyList<string> DisableMods(string gameDir, IEnumerable<string> jarNames)
    {
        var modsDir = Path.Combine(gameDir, "mods");
        var disabledDir = Path.Combine(gameDir, "mods-disabled");

        var moved = new List<string>();

        if (!Directory.Exists(modsDir)) return moved;

        foreach (var jar in jarNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // 只接受纯文件名：外部传进来的东西绝不参与拼路径，避免 ../ 这类花样
            if (string.IsNullOrWhiteSpace(jar) || jar.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;

            var source = Path.Combine(modsDir, jar);
            if (!File.Exists(source)) continue;

            try
            {
                Directory.CreateDirectory(disabledDir);

                var target = Path.Combine(disabledDir, jar);
                if (File.Exists(target)) File.Delete(target);

                File.Move(source, target);
                moved.Add(jar);
            }
            catch
            {
                // 单个文件挪不动（被占用）不影响其他，返回值里就不会出现它
            }
        }

        return moved;
    }

    // ── 规则 ────────────────────────────────────────────

    private static List<CrashFinding> RunRules(
        string text,
        string reportText,
        string? logTail,
        string gameDir)
    {
        var findings = new List<CrashFinding>();
        var suspects = CollectSuspects(text, gameDir);

        void Add(CrashFinding finding)
        {
            // 同一个标题只留第一条（多个规则可能命中同一现象）
            if (findings.Any(f => f.Title == finding.Title)) return;
            findings.Add(finding);
        }

        // ① 爆栈 —— 必须排在普通 OOM 前面，否则会被误判成「内存不够，调大 Xmx」
        var stackOverflow = FindEvidence(text, "Out of stack space|StackOverflowError", 2);
        if (stackOverflow.Count > 0)
        {
            Add(new CrashFinding(
                "栈溢出（不是内存不够）",
                CrashSeverity.Fatal,
                "报的是 OutOfMemoryError: Out of stack space，但这不是「堆内存不够」，"
                + "而是某段代码在无限递归（常见于模组之间互相注入、或全局 JVM 参数干扰了加载器）。"
                + "把 -Xmx 调大对这种情况没有用。",
                "把最近加的模组挪出 mods 目录再试；如果是整合包，先只留优化类模组。"
                + "另外：启动器给 Fabric 挂的 -javaagent 类补丁会干扰 NeoForge 的 MixinExtras，"
                + "本项目已改成只给 Fabric 挂，若你手动改过启动参数，先去掉。",
                stackOverflow,
                Array.Empty<string>()));
        }

        // ② 真 OOM
        var oom = FindEvidence(text, "java\\.lang\\.OutOfMemoryError", 3);
        if (oom.Count > 0 && stackOverflow.Count == 0)
        {
            Add(new CrashFinding(
                "内存不够（堆溢出）",
                CrashSeverity.Fatal,
                "游戏在运行中申请不到堆内存。整合包模组多、视野开得大时很容易撞上。",
                "把「最大内存」往右调（64 位 Java 下可以给到物理内存的一半左右），"
                + "同时关掉一些吃内存的模组或降低渲染距离。",
                oom,
                Array.Empty<string>()));
        }

        // ③ Java 版本
        var javaTooOld = FindEvidence(text, "UnsupportedClassVersionError", 3);
        if (javaTooOld.Count > 0)
        {
            var (required, current) = DescribeJava(text);
            Add(new CrashFinding(
                "Java 版本不对",
                CrashSeverity.Fatal,
                $"这个版本需要 {required}，但当前用的是 {current}。Java 太旧连类文件都读不了"
                + "（报错里会写「class file version 65.0」这种，65 = Java 21）。",
                "让这个版本用自动下载的 Java（默认就是自动），"
                + "或者自己装好对应版本的 JDK 再手动指定。",
                javaTooOld,
                Array.Empty<string>()));
        }

        // ④ Mixin 注入失败 —— 今天踩过的那个坑
        var mixin = FindEvidence(text,
            "InvalidInjectionException|MixinApplyError|MixinTransformerError|InvalidMixinException", 3);
        if (mixin.Count > 0)
        {
            var names = suspects.Count > 0
                ? string.Join("、", suspects.Take(3))
                : "（没定位到具体模组）";

            Add(new CrashFinding(
                "模组注入失败（Mixin）",
                CrashSeverity.Fatal,
                "模组往游戏代码里注入的钩子对不上：要么模组版本和这个游戏/加载器版本不匹配，"
                + "要么有别的全局补丁改了注入目标。嫌疑：" + names,
                "先试着禁用上面这几个模组；"
                + "如果是整合包，确认它自带的是不是这个游戏版本的模组版本。"
                + "这类错误一次只报一个，通常得挨个排除。",
                mixin,
                suspects.Take(3).ToList()));
        }

        // ⑤ 缺前置 / 依赖
        var missing = FindBlockAfter(text,
            "Missing or unsupported mandatory dependencies|ModResolutionException", 4);
        if (missing.Count > 0)
        {
            Add(new CrashFinding(
                "缺前置模组",
                CrashSeverity.Fatal,
                "有模组声明了必须存在的依赖，但 mods 里找不到。整合包少下一个前置是很常见的原因。",
                "看证据里「Mod ID: 'xxx'」的模组名，去内容下载里搜它装上；"
                + "如果是整合包装的，重启启动器再装一次让它补依赖。",
                missing,
                Array.Empty<string>()));
        }

        // ⑥ 类缺失
        var noClass = FindEvidence(text, "NoClassDefFoundError|ClassNotFoundException", 3);
        if (noClass.Count > 0 && missing.Count == 0)
        {
            Add(new CrashFinding(
                "找不到类（模组用的 API 不存在）",
                CrashSeverity.Fatal,
                "模组引用了一个当前环境里没有的类：要么前置没装，要么那个模组是给别的游戏/加载器版本写的。",
                "看证据里的类名，先查它属于哪个模组；"
                + "如果类名里带着被移除的接口（比如 NeoForge 新版删掉的 Event），说明这个模组没跟上版本。",
                noClass,
                suspects.Take(3).ToList()));
        }

        // ⑦ 文件损坏
        var corrupt = FindEvidence(text,
            "zip file is empty|invalid LOC header|ZipException|Invalid or corrupt jarfile|error reading zip file", 3);
        if (corrupt.Count > 0)
        {
            Add(new CrashFinding(
                "文件损坏",
                CrashSeverity.Fatal,
                "有 jar 是坏的或下到一半（0 字节、zip 头损坏都算）。这类报错通常指向很奇怪的类名，很容易误导。",
                "点主界面的「校验文件」跑一遍深度校验，它会算出坏文件并一键重下。",
                corrupt,
                Array.Empty<string>()));
        }

        // ⑧ 重复模组
        var duplicate = FindEvidence(text, "DuplicateModsFoundException|Found duplicate mods", 2);
        if (duplicate.Count > 0)
        {
            Add(new CrashFinding(
                "同一个模组装了两份",
                CrashSeverity.Fatal,
                "mods 目录里有重复的模组（常见于整合包和自己手装的撞了）。",
                "把重复的那个删掉，或挪进 mods-disabled。证据里能看到重复的是哪个。",
                duplicate,
                Array.Empty<string>()));
        }

        // ⑨ 显卡 / 窗口
        var graphics = FindEvidence(text,
            "GLFW error|Failed to create window|Pixel format not accelerated|WGL|opengl32", 2);
        if (graphics.Count > 0)
        {
            Add(new CrashFinding(
                "创建游戏窗口失败（显卡/驱动）",
                CrashSeverity.Fatal,
                "OpenGL 上下文没建起来。远程桌面、显卡驱动太旧、双显卡切换错都会这样。",
                "更新显卡驱动；用远程桌面时改成本机操作；"
                + "双显卡机器在显卡控制面板里指定用独立显卡跑 java.exe。",
                graphics,
                Array.Empty<string>()));
        }

        // ⑩ 模组自己在初始化里抛异常
        var unsupported = FindEvidence(text, "UnsupportedOperationException", 2);
        if (unsupported.Count > 0)
        {
            Add(new CrashFinding(
                "模组初始化时自己报错",
                CrashSeverity.Fatal,
                "某个模组在初始化阶段抛了异常，通常是它自己跟这个版本不兼容（不是你的配置问题）。",
                "按证据里出现的模组名禁用它，等作者更新。",
                unsupported,
                suspects.Take(3).ToList()));
        }

        // ⑪ 权限
        var denied = FindEvidence(text, "AccessDeniedException|Access is denied|拒绝访问", 2);
        if (denied.Count > 0)
        {
            Add(new CrashFinding(
                "文件权限被拒",
                CrashSeverity.Warning,
                "游戏想写某个文件被系统拒绝了（杀毒软件、受控文件夹访问、或目录只读都可能）。",
                "把游戏目录加进杀毒软件白名单；确认目录不是只读。",
                denied,
                Array.Empty<string>()));
        }

        return findings;
    }

    /// <summary>
    /// 没有崩溃报告、规则也没命中时给的那一条。
    /// </summary>
    /// <remarks>
    /// 这窗口是用户主动点开的，<b>什么都不显示比说错更糟</b> —— 会让人以为功能是坏的。
    /// 有 ERROR 就把 ERROR 摊出来，只有 WARN 就说「能跑，但有几条警告」。
    /// </remarks>
    private static CrashFinding BuildNormalRunFinding(string logTail, DateTime? logTime)
    {
        var errorCount = CountLevel(logTail, "ERROR");
        var warnCount = CountLevel(logTail, "WARN");

        var when = logTime is { } t ? $"（{t:MM-dd HH:mm}）" : string.Empty;

        if (errorCount > 0)
        {
            return new CrashFinding(
                $"日志里有 {errorCount} 条报错，但没崩",
                CrashSeverity.Warning,
                $"最近一次启动{when}跑完了，没有生成崩溃报告，不过 latest.log 里有 {errorCount} 条 ERROR、"
                + $"{warnCount} 条 WARN。游戏能进不代表这些报错都无所谓 —— "
                + "有一部分（比如连不上认证/Realms 服务器）确实可以忽略，但要是某项功能不对劲，就得看它们。",
                "对照下面的报错原文：如果只是「Failed to fetch user properties / Couldn't connect to realms」"
                + "这类联网失败，不影响单机玩；如果是模组相关的，再按原文里提到的名字去查。",
                ExtractLevelLines(logTail, "ERROR", MaxEvidenceLines),
                Array.Empty<string>());
        }

        return new CrashFinding(
            "最近一次启动正常，没有崩溃",
            CrashSeverity.Info,
            $"没有找到崩溃报告，latest.log{when} 里也没有 ERROR，只有 {warnCount} 条提示级的警告。"
            + "也就是说游戏上次是自己正常退出或被关掉的，不是崩的。",
            "如果你刚遇到过闪退，说明日志已经被下一次启动覆盖了 —— "
            + "下次崩完别急着再开游戏，先点「崩溃分析」。"
            + "也可以用「打开崩溃报告」看看 crash-reports 目录里有没有更早的文件。",
            ExtractLevelLines(logTail, "WARN", MaxEvidenceLines),
            Array.Empty<string>());
    }

    /// <summary>数一下某个级别的行数。</summary>
    private static int CountLevel(string text, string level)
        => Regex.Matches(text, @"/" + level + @"\]").Count;

    /// <summary>摘出某个级别的几行原文（已去掉时间戳和线程前缀）。</summary>
    private static List<string> ExtractLevelLines(string text, string level, int max)
    {
        var result = new List<string>();

        foreach (Match match in Regex.Matches(text, @".*?/" + level + @"\].*", RegexOptions.Multiline))
        {
            if (result.Count >= max) break;

            var cleaned = Clean(match.Value);
            if (!string.IsNullOrWhiteSpace(cleaned) && !result.Contains(cleaned)) result.Add(cleaned);
        }

        return result;
    }

    /// <summary>找证据行，最多 <paramref name="max"/> 条，都做长度裁剪。</summary>
    private static List<string> FindEvidence(string text, string pattern, int max)
    {
        var result = new List<string>();

        foreach (Match match in Regex.Matches(text, pattern))
        {
            if (result.Count >= max) break;

            var line = LineAround(text, match.Index);
            var cleaned = Clean(line);

            if (!string.IsNullOrWhiteSpace(cleaned) && !result.Contains(cleaned))
                result.Add(cleaned);
        }

        return result;
    }

    /// <summary>
    /// 命中位置<b>连同后面几行</b>一起取。
    /// </summary>
    /// <remarks>
    /// 「Missing or unsupported mandatory dependencies:」这类报错，关键信息（缺的是哪个 modId）
    /// 全在<b>下一行</b>，只取命中行等于什么都没说。
    /// </remarks>
    private static List<string> FindBlockAfter(string text, string pattern, int maxLines)
    {
        var result = new List<string>();

        var match = Regex.Match(text, pattern);
        if (!match.Success) return result;

        var start = text.LastIndexOf('\n', Math.Max(0, match.Index - 1));
        start = start < 0 ? 0 : start + 1;

        foreach (var raw in text[start..].Split('\n'))
        {
            if (result.Count >= maxLines) break;

            var cleaned = Clean(raw);

            if (string.IsNullOrWhiteSpace(cleaned))
            {
                if (result.Count > 0) break; // 空行 = 这一段结束
                continue;
            }

            result.Add(cleaned);
        }

        return result;
    }

    /// <summary>
    /// 从日志里找「可能有问题的是哪个 jar」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 四路来源，优先级从高到低：
    /// </para>
    /// <list type="number">
    ///   <item><c>Mod File:</c> —— Forge / NeoForge 崩溃报告直接写，最权威；</item>
    ///   <item>路径里的 <c>mods\xxx.jar</c>；</item>
    ///   <item>正文里提到的 jar 文件名，<b>但必须真的在 mods 目录里</b>才算
    ///         （这条挡住「日志里随便什么 .jar 都被当嫌疑人」）；</item>
    ///   <item>拿 jar 名里的词根去对报错文本（<c>lithium</c> ⊂ 报错），最后手段。</item>
    /// </list>
    /// <para>
    /// <b>宁可少报，不能乱指</b> —— 指错了用户会白删一个好模组，甚至把能玩的环境搞崩。
    /// 所以第 3 条只在文件确实存在时才认。
    /// </para>
    /// </remarks>
    private static List<string> CollectSuspects(string text, string gameDir)
    {
        var suspects = new List<string>();

        void Add(string name)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0) return;
            if (!trimmed.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) trimmed += ".jar";
            if (!suspects.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) suspects.Add(trimmed);
        }

        // ① Forge / NeoForge 崩溃报告里的 Mod File
        foreach (Match m in Regex.Matches(text, @"Mod File:\s*(?<name>[^\\/\r\n]+\.jar)", RegexOptions.IgnoreCase))
            Add(m.Groups["name"].Value);

        // ② 堆栈里出现的 mods 路径
        foreach (Match m in Regex.Matches(text, @"mods[\\/](?<name>[^\\/\r\n]+?\.jar)", RegexOptions.IgnoreCase))
            Add(m.Groups["name"].Value);

        var modsDir = Path.Combine(gameDir, "mods");
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(modsDir))
        {
            foreach (var path in Directory.GetFiles(modsDir, "*.jar"))
            {
                if (Path.GetFileName(path) is { Length: > 0 } name) installed.Add(name);
            }
        }

        // ③ 正文里提到的 jar，必须真是已装的模组才认
        if (installed.Count > 0)
        {
            foreach (Match m in Regex.Matches(text, @"(?<name>[A-Za-z0-9][A-Za-z0-9._+\-]*\.jar)", RegexOptions.IgnoreCase))
            {
                var name = m.Groups["name"].Value;
                if (installed.Contains(name)) Add(name);
                if (suspects.Count >= 5) break;
            }
        }

        if (suspects.Count > 0) return suspects;

        // ④ 词根模糊匹配（只有前面全落空时才用，误报率最高）
        foreach (var jar in installed)
        {
            var stem = jar.Split('-', '+', '_')[0];
            if (stem.Length < 4) continue;

            if (text.Contains(stem, StringComparison.OrdinalIgnoreCase))
                Add(jar);

            if (suspects.Count >= 5) break;
        }

        return suspects;
    }

    // ── 文件读取 ────────────────────────────────────────

    private static string? FindNewestCrashReport(string gameDir)
    {
        var dir = Path.Combine(gameDir, "crash-reports");
        if (!Directory.Exists(dir)) return null;

        try
        {
            return Directory.GetFiles(dir, "*.txt")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 给 AI 分析用的日志摘录：崩溃报告全文（有的话）+ latest.log 尾部。
    /// </summary>
    /// <remarks>
    /// 和 <see cref="Analyze"/> 分开是因为用途不同：规则引擎要的是整个文本用来正则匹配，
    /// 而发到网络上必须<b>有硬上限</b> —— 日志几 MB 全发出去既慢又贵。
    /// 崩溃报告优先保留（信息密度最高），剩下的额度给日志尾部。
    /// </remarks>
    /// <param name="gameDir">游戏目录。</param>
    /// <param name="maxChars">最多返回多少字符。</param>
    public static string BuildAiContext(string gameDir, int maxChars = 16_000)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return string.Empty;

        var sb = new StringBuilder();

        var crashReport = FindNewestCrashReport(gameDir);
        if (crashReport is not null && SafeRead(crashReport) is { Length: > 0 } report)
        {
            sb.AppendLine("### crash-report");
            sb.AppendLine(report.Length <= maxChars / 2 ? report : report[..(maxChars / 2)]);
            sb.AppendLine();
        }

        if (ReadLogTail(Path.Combine(gameDir, "logs", "latest.log")) is { Length: > 0 } log)
        {
            sb.AppendLine("### latest.log（尾部）");
            sb.AppendLine(log);
        }

        var text = sb.ToString();
        return text.Length <= maxChars ? text : text[^maxChars..];
    }

    /// <summary>某个版本的 mods 目录里有哪些 jar（只要文件名，给 AI 看够了）。</summary>
    public static IReadOnlyList<string> ListModFiles(string gameDir)
    {
        var modsDir = Path.Combine(gameDir, "mods");

        try
        {
            if (!Directory.Exists(modsDir)) return Array.Empty<string>();

            return Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? ReadLogTail(string logPath)
    {
        try
        {
            if (!File.Exists(logPath)) return null;

            // latest.log 可能几十 MB，只取最后一段：用 FileShare.ReadWrite 打开，
            // 游戏还开着的时候也能读（否则会撞「文件被占用」）
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var lines = new Queue<string>(LogTailLines);
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > LogTailLines) lines.Dequeue();
            }

            return string.Join('\n', lines);
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractHeader(string reportText, string key)
    {
        if (string.IsNullOrEmpty(reportText)) return null;

        var match = Regex.Match(reportText, $@"^{Regex.Escape(key)}:\s*(?<value>.+?)\s*$",
            RegexOptions.Multiline);

        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static string? ExtractDescription(string reportText) => ExtractHeader(reportText, "Description");

    /// <summary>
    /// 算出「这个版本该用哪个 Java」和「现在用的是哪个」。
    /// </summary>
    /// <remarks>
    /// 要求按<b>游戏版本</b>推，不能看当前 Java 版本 —— 用户现在的 Java 必然是错的，
    /// 知道它是几没有意义，有用的是「应该装几」。当前版本只用来把话说全。
    /// </remarks>
    private static (string Required, string Current) DescribeJava(string text)
    {
        var required = "更新的 Java";

        var mc = Regex.Match(text, @"Minecraft Version:\s*(?<v>[\w.\-]+)");
        if (mc.Success)
        {
            var version = mc.Groups["v"].Value;
            required = version switch
            {
                _ when version.StartsWith("1.20.5", StringComparison.Ordinal) ||
                       version.StartsWith("1.21", StringComparison.Ordinal) ||
                       version.StartsWith("1.22", StringComparison.Ordinal) => "Java 21",
                _ when version.StartsWith("1.17", StringComparison.Ordinal) ||
                       version.StartsWith("1.18", StringComparison.Ordinal) ||
                       version.StartsWith("1.19", StringComparison.Ordinal) ||
                       version.StartsWith("1.20", StringComparison.Ordinal) => "Java 17",
                _ => "Java 8",
            };
        }

        var java = Regex.Match(text, @"Java Version:\s*(?<v>[\d._]+)");
        var current = java.Success ? $"Java {java.Groups["v"].Value}" : "旧版 Java";

        return (required, current);
    }

    /// <summary>取命中位置所在的那一行。</summary>
    private static string LineAround(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1));
        start = start < 0 ? 0 : start + 1;

        var end = text.IndexOf('\n', Math.Min(index, text.Length - 1));
        end = end < 0 ? text.Length : end;

        return text[start..end];
    }

    /// <summary>证据行做裁剪：日志行经常长得离谱，窗口里放不下。</summary>
    private static string Clean(string line)
    {
        var trimmed = line.Trim();

        // Forge 的日志前缀带时间戳和线程名，留着没意义
        trimmed = Regex.Replace(trimmed, @"^\[\d{2}:\d{2}:\d{2}\]\s*", string.Empty);
        trimmed = Regex.Replace(trimmed, @"^\[[^\]]{0,40}\]\s*", string.Empty);

        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }

    /// <summary>最多带几条证据（供界面按需截断）。</summary>
    public static int MaxEvidencePerFinding => MaxEvidenceLines;
}
