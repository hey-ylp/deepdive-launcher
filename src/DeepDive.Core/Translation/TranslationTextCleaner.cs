using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DeepDive.Core.Translation;

/// <summary>
/// 擦干净机翻返回的脏文本，再把 MC 术语改回官方译名。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这一步</b>：MyMemory 的数据来自各方语料，返回的译文经常夹带 HTML。
/// 2026-10-29 实测翻 <c>Sodium</c> 直接返回：
/// </para>
/// <code>
/// &lt;g id="1070"&gt;★&lt;/g&gt;&lt;g id="1073"&gt;钠&lt;/g&gt;
/// </code>
/// <para>
/// 原样塞进界面就是一堆标签，比不翻还糟。
/// </para>
/// </remarks>
public static class TranslationTextCleaner
{
    /// <summary>带属性或纯标签一律删；<c>&lt;br&gt;</c> 换成空格，别把前后词黏一起。</summary>
    private static readonly Regex TagPattern = new(@"<[^>]{0,40}>", RegexOptions.Compiled);

    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// MC 官方中文译名对照。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 机翻在 MC 术语上稳定翻车，实测得到的真实例子：
    /// <c>biomes</c> → 「生物群落」（官方是<b>生物群系</b>）、
    /// <c>nether dimension</c> → 「勘探地下空间」（官方是<b>下界维度</b>）、
    /// <c>inventory</c> → 「库存」（官方是<b>物品栏</b>）、
    /// <c>world generation</c> → 「世界一代」（应该是<b>世界生成</b>）。
    /// </para>
    /// <para>
    /// 顺序有讲究：<b>长词条排前面</b>，否则「勘探地下空间」会先被「地下空间」吃掉一半，
    /// 剩个「勘探下界」，读起来更怪。
    /// </para>
    /// </remarks>
    private static readonly (string Wrong, string Right)[] TermFixes =
    {
        ("勘探地下空间", "下界维度"),
        ("下界空间", "下界维度"),
        ("世界一代", "世界生成"),
        ("生物群落", "生物群系"),
        ("定制洞穴", "自定义洞穴"),
        ("保留你的库存", "保留你的物品栏"),
        ("你的库存", "你的物品栏"),
        ("库存中", "物品栏中"),
        ("合成台", "工作台"),
        ("工作板凳", "工作台"),
        ("红石石", "红石"),
        ("模组组", "模组"),
        ("服务器端", "服务端"),
        ("客户端侧", "客户端"),

        // vanilla 在 MC 语境里一律指「原版」，机翻稳定翻成「香草」。
        // 实测原文：Reintroduces vanilla-like smooth lighting
        //          → 将类似香草的光滑照明重新引入      ← 玩家看不懂「香草」是什么
        ("类似香草", "原版风格"),
        ("香草的", "原版的"),
        ("香草风味", "原版风格"),
        ("香草味", "原版风"),
        ("香草", "原版"),

        // 方块与地形
        ("污垢路径", "土径"),
        ("污垢小径", "土径"),
        ("泥土路径", "土径"),
        ("非完整块", "非完整方块"),
        ("完整块", "完整方块"),
        ("块状物", "方块"),

        // 性能相关
        ("微停顿", "卡顿"),
        ("微型卡顿", "卡顿"),
        ("帧速率", "帧率"),
        ("帧数率", "帧率"),
    };

    /// <summary>清理 + 修正。返回 <c>null</c> 表示这条译文没救了，别用。</summary>
    public static string? Clean(string? translated, string original)
    {
        if (string.IsNullOrWhiteSpace(translated)) return null;

        // 1. 删 HTML 标签（<g id="..">、<b>、<br> 都在这条规则里）
        var text = TagPattern.Replace(translated, " ");

        // 2. 反转义实体：&amp; &quot; &#39; &nbsp;
        text = WebUtility.HtmlDecode(text);

        // 3. 压掉多余的空白
        text = Spaces.Replace(text, " ");
        text = text.Trim();

        if (text.Length == 0) return null;

        // 4. 清理后如果跟原文一样，说明根本没翻
        if (string.Equals(text, original.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;

        // 5. MC 术语修正
        var builder = new StringBuilder(text);
        foreach (var (wrong, right) in TermFixes)
        {
            if (wrong == right) continue;
            builder.Replace(wrong, right);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 这条路值不值得翻。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>单词不翻</b>：没有空格的短文本几乎都是模组名（<c>Sodium</c>、<c>Create</c>、<c>JEI</c>），
    /// 机翻会给出诸如「钠」这种结果 —— 玩家看到的模组叫「钠」，比看英文还懵。
    /// </para>
    /// <para>
    /// 简介都是完整句子，必然带空格，所以这条规则不会漏掉真正需要翻的内容。
    /// </para>
    /// </remarks>
    public static bool WorthTranslating(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();

        // 太短的不值得（"Cool!" "Yes" 之类）
        if (trimmed.Length < 12) return false;

        // 没有空格 = 单个词/标识符，翻出来只会是灾难
        if (!trimmed.Contains(' ')) return false;

        // 全是符号/数字的不用翻
        if (!trimmed.Any(char.IsLetter)) return false;

        return true;
    }
}
