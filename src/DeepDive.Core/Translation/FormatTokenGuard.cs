using System.Text.RegularExpressions;

namespace DeepDive.Core.Translation;

/// <summary>
/// 把「翻译时绝对不能动」的东西换成哨兵，翻完再换回来。
/// </summary>
/// <remarks>
/// <para>
/// 模组的语言文件里混着大量机器不该碰的东西，翻坏了游戏里就是一堆方块字或者直接崩：
/// </para>
/// <list type="bullet">
///   <item><c>§a</c> <c>§c</c> <c>§l</c> —— 颜色 / 粗体之类的格式码，少一个整段变白。</item>
///   <item><c>{0}</c> <c>{1}</c> <c>{name}</c> —— Java 的格式化占位符，删了会抛异常。</item>
///   <item><c>%s</c> <c>%d</c> <c>%1$s</c> —— printf 风格占位符。</item>
/// </list>
/// <para>
/// 换行<b>不</b>保护，理由见 <see cref="TokenPattern"/> 的注释。
/// </para>
/// <b>为什么不用 <see cref="PlaceholderGuard"/></b>：那个是保护「整词」（Sodium、Forge 这类专有名词），
/// 靠词边界做正则。这里的格式码没有词边界，<c>§a</c> 前后可能紧贴汉字，规则完全不同，混在一起会互相干扰。
/// </para>
/// <para>
/// 哨兵沿用 <c>[[F0]]</c> 这种方括号写法 —— 2026-10-29 实测过 MyMemory 会原样保留 <c>[[T1]]</c>，
/// DeepSeek 更稳。但不同引擎还是可能在括号里加空格，所以还原时用宽容匹配。
/// </para>
/// </remarks>
public static class FormatTokenGuard
{
    /// <summary>
    /// 一次扫出所有需要保护的东西。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 顺序在正则里是有意义的：<c>%1$s</c> 必须整体命中，否则会先被 <c>%s</c> 吃掉一半，
    /// 剩下个孤零零的 <c>1$</c>。
    /// </para>
    /// <para>
    /// <b>换行不在保护范围里</b>，这是踩过坑之后的决定。英文的换行是按字符宽度硬折的
    /// （Sodium 那条配置损坏提示短短一段就有 8 个 <c>\n</c>），中文宽度完全不同，
    /// 强行要求换行数量一致只会让整条译文作废 —— 实测就是这样：
    /// 模型按中文重排了换行，我们判定「8 个 vs 5 个」不符，白白丢掉一条翻译。
    /// 换行位置不对最多是显示得挤一点，而 <c>%s</c> 少了会让游戏抛异常，两者不是一个量级。
    /// </para>
    /// </remarks>
    private static readonly Regex TokenPattern = new(
        @"§.|%(?:\d+\$)?[sdf]|\{[^{}\r\n]{1,24}\}",
        RegexOptions.Compiled);

    /// <summary>宽容匹配：<c>[[F0]]</c>、<c>[[ F0 ]]</c>、<c>[F0]</c> 都认。</summary>
    private static readonly Regex RestorePattern = new(
        @"\[\s*\[\s*F\s*(\d{1,3})\s*\]\s*\]|\[\s*F\s*(\d{1,3})\s*\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>把格式码、占位符、换行换成 <c>[[F0]]</c>、<c>[[F1]]</c>…</summary>
    /// <returns>替换后的文本 + 下标到原文的映射（还原时要按顺序填回去）。</returns>
    public static (string Text, IReadOnlyList<string> Map) Protect(string text)
    {
        var map = new List<string>();
        if (string.IsNullOrEmpty(text)) return (text, map);

        var result = TokenPattern.Replace(text, match =>
        {
            var index = map.Count;
            map.Add(match.Value);
            return $"[[F{index}]]";
        });

        return (result, map);
    }

    /// <summary>把哨兵换回原来的格式码。找不到对应下标就原样保留，宁可留个标记也不吞掉。</summary>
    public static string Restore(string translated, IReadOnlyList<string> map)
    {
        if (string.IsNullOrEmpty(translated) || map.Count == 0) return translated;

        return RestorePattern.Replace(translated, match =>
        {
            var raw = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

            return int.TryParse(raw, out var index) && index >= 0 && index < map.Count
                ? map[index]
                : match.Value;
        });
    }

    /// <summary>
    /// 检查译文里的格式符是否和原文一一对上。
    /// </summary>
    /// <remarks>
    /// 机翻偶尔会把 <c>[[F2]]</c> 整个吃掉或者复制一份，这种译文宁可不用 ——
    /// 进游戏轻则文字错位，重则 <c>String.format</c> 抛 <c>MissingFormatArgumentException</c> 让界面崩掉。
    /// </remarks>
    /// <returns>问题描述；没问题返回 <c>null</c>。</returns>
    public static string? Validate(string original, string translated)
    {
        var expected = ExtractTokens(original);
        var actual = ExtractTokens(translated);

        if (expected.Count == 0) return null;

        if (actual.Count != expected.Count)
        {
            return $"格式符数量不符：原文 {expected.Count} 个（{string.Join("", expected)}），" +
                   $"译文 {actual.Count} 个（{string.Join("", actual)}）";
        }

        for (var i = 0; i < expected.Count; i++)
        {
            if (expected[i] != actual[i])
            {
                return $"格式符顺序变了：第 {i + 1} 个原文是 {expected[i]}，译文是 {actual[i]}";
            }
        }

        return null;
    }

    /// <summary>按出现顺序取出文本里所有的格式符 / 占位符 / 换行。</summary>
    public static List<string> ExtractTokens(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;

        foreach (Match match in TokenPattern.Matches(text))
        {
            result.Add(match.Value);
        }

        return result;
    }
}
