using System.Text.RegularExpressions;

namespace DeepDive.Core.Translation;

/// <summary>
/// 用占位符把「不准翻的词」保护起来，翻完再还原。
/// </summary>
/// <remarks>
/// <para>
/// 实测（2026-10-29，MyMemory）<c>[[T1]]</c> 这种方括号标记会被完整保留：
/// </para>
/// <code>
/// A [[T1]] addon that adds features that should not be in [[T1]].
///   → 添加不应出现在[[T1]]中的功能的[[T1]]插件。
/// </code>
/// <para>
/// 但不同的机翻引擎处理标记的方式不一样，有的会在方括号里加空格、有的会少一侧括号，
/// 所以还原时用宽容匹配，别指望它一个字符都不改。
/// </para>
/// </remarks>
public static class PlaceholderGuard
{
    /// <summary>宽容匹配：<c>[[T1]]</c>、<c>[[ T1 ]]</c>、<c>[T1]</c> 都认。</summary>
    private static readonly Regex RestorePattern = new(
        @"\[\s*\[\s*T\s*(\d{1,3})\s*\]\s*\]|\[\s*T\s*(\d{1,3})\s*\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 把 <paramref name="keepAsIs"/> 里的词换成 <c>[[T0]]</c>、<c>[[T1]]</c>…
    /// </summary>
    /// <returns>替换后的文本 + 下标到原词的映射（还原时要用）。</returns>
    public static (string Text, IReadOnlyList<string> Map) Protect(
        string text,
        IReadOnlyList<string> keepAsIs)
    {
        var map = new List<string>();
        if (string.IsNullOrEmpty(text) || keepAsIs.Count == 0) return (text, map);

        var result = text;

        // 长词优先：项目名叫 "Sodium Extra" 时，不先把它整体换掉，
        // "Extra" 会被当成普通英文单词翻成「额外」
        foreach (var word in keepAsIs
                     .Where(w => !string.IsNullOrWhiteSpace(w) && w.Trim().Length >= 3)
                     .Select(w => w.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(w => w.Length))
        {
            // 前后不能紧邻字母数字，避免 "Forge" 命中 "NeoForge" 或 "Forged"
            var pattern = $@"(?<![A-Za-z0-9]){Regex.Escape(word)}(?![A-Za-z0-9])";

            if (!Regex.IsMatch(result, pattern, RegexOptions.IgnoreCase)) continue;

            var index = map.Count;
            map.Add(word);

            result = Regex.Replace(
                result,
                pattern,
                _ => $"[[T{index}]]",
                RegexOptions.IgnoreCase);
        }

        return (result, map);
    }

    /// <summary>把占位符换回原词。找不到对应下标就保留原样，宁可留个标记也不吞掉。</summary>
    public static string Restore(string translated, IReadOnlyList<string> map)
    {
        if (string.IsNullOrEmpty(translated) || map.Count == 0) return translated;

        return RestorePattern.Replace(translated, match =>
        {
            // 两种写法命中在不同分组里
            var raw = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

            return int.TryParse(raw, out var index) && index >= 0 && index < map.Count
                ? map[index]
                : match.Value;
        });
    }
}
