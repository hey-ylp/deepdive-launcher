using DeepDive.Core.Modrinth;

namespace DeepDive.Core.Translation;

/// <summary>
/// 把 Modrinth 的分类标签翻成中文。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这块不送机翻</b>：分类名总共 100 来个，是封闭集合，可以穷举。
/// 送机翻既慢又贵（每页都要翻一遍），而且会翻错 —— <c>kitchen-sink</c> 机翻成
/// 「厨房水槽」，实际意思是「大杂烩整合包」；<c>potato</c> 是个梗，指「土豆机也能跑」的低配光影。
/// </para>
/// <para>
/// 这份表是照 <c>GET /tag/category</c>（2026-10-29 实测）逐个对的，不是猜的。
/// 新增分类时查不到就原样显示英文，不会显示空白。
/// </para>
/// </remarks>
public static class CategoryLocalizer
{
    private static readonly Dictionary<string, string> Zh = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── 模组（mod）─────────────────────────────
        ["adventure"] = "冒险",
        ["cursed"] = "阴间",
        ["decoration"] = "装饰",
        ["economy"] = "经济",
        ["equipment"] = "装备",
        ["food"] = "食物",
        ["game-mechanics"] = "玩法机制",
        ["library"] = "前置库",
        ["magic"] = "魔法",
        ["management"] = "管理",
        ["minigame"] = "小游戏",
        ["mobs"] = "生物",
        ["optimization"] = "优化",
        ["social"] = "社交",
        ["storage"] = "存储",
        ["technology"] = "科技",
        ["transportation"] = "交通",
        ["utility"] = "实用工具",
        ["worldgen"] = "世界生成",

        // ── 整合包（modpack）───────────────────────
        ["challenging"] = "高难度",
        ["combat"] = "战斗",
        ["kitchen-sink"] = "大杂烩",
        ["lightweight"] = "轻量",
        ["multiplayer"] = "多人",
        ["quests"] = "任务线",

        // ── 资源包（resourcepack）──────────────────
        ["8x-"] = "8x 以下",
        ["16x"] = "16x",
        ["32x"] = "32x",
        ["48x"] = "48x",
        ["64x"] = "64x",
        ["128x"] = "128x",
        ["256x"] = "256x",
        ["512x+"] = "512x 以上",
        ["audio"] = "音效",
        ["blocks"] = "方块",
        ["core-shaders"] = "核心光影",
        ["entities"] = "实体",
        ["environment"] = "环境",
        ["fonts"] = "字体",
        ["gui"] = "界面",
        ["items"] = "物品",
        ["locale"] = "语言",
        ["modded"] = "模组兼容",
        ["models"] = "模型",
        ["realistic"] = "写实",
        ["simplistic"] = "简约",
        ["themed"] = "主题",
        ["tweaks"] = "微调",
        ["vanilla-like"] = "原版风",

        // ── 光影包（shader）────────────────────────
        ["atmosphere"] = "氛围",
        ["bloom"] = "泛光",
        ["cartoon"] = "卡通",
        ["colored-lighting"] = "彩色光照",
        ["fantasy"] = "奇幻",
        ["foliage"] = "植被",
        ["high"] = "高配",
        ["medium"] = "中配",
        ["low"] = "低配",
        ["potato"] = "极低配",
        ["path-tracing"] = "光追",
        ["pbr"] = "PBR 材质",
        ["reflections"] = "反射",
        ["screenshot"] = "截图向",
        ["semi-realistic"] = "半写实",
        ["shadows"] = "阴影",
    };

    /// <summary>翻一个分类名。查不到就原样返回（宁可显英文，也不显示空白）。</summary>
    public static string Chinese(string category) =>
        Zh.TryGetValue(category, out var value) ? value : category;

    /// <summary>这个词有没有中文对照。</summary>
    public static bool HasChinese(string category) => Zh.ContainsKey(category);

    /// <summary>
    /// 挑出「功能分类」并翻成中文。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 必须把加载器排掉：Sodium 的 <c>categories</c> 是
    /// <c>[fabric, neoforge, optimization, quilt]</c>，加载器已经在标题行单独显示了，
    /// 再混进分类徽章里就成了「Fabric 优化 Fabric」这种废话。
    /// </para>
    /// <para>
    /// 只取前 <paramref name="max"/> 个 —— 有的项目标了 8 个分类，全铺出来会把标题行挤爆。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Functional(
        IEnumerable<string> categories,
        ContentTypeInfo typeInfo,
        int max = 3)
    {
        var result = new List<string>();

        foreach (var category in categories)
        {
            if (result.Count >= max) break;

            // 加载器不算功能分类
            if (typeInfo.Loaders.Contains(category, StringComparer.OrdinalIgnoreCase))
                continue;

            // 「原版风」这类在两个类型里都有的，统一走同一份表
            var label = Chinese(category);
            if (!result.Contains(label)) result.Add(label);
        }

        return result;
    }
}
