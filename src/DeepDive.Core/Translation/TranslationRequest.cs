namespace DeepDive.Core.Translation;

/// <summary>
/// 一次翻译请求：要翻什么，以及哪些词不准翻。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须带「不准翻」的名单</b>：机翻会把模组名当普通单词处理。实测真事：
/// </para>
/// <code>
/// Alternative Options Menu for Sodium
///   → 钠的替代选项菜单          ← Sodium 被当成化学元素「钠」
/// </code>
/// <para>
/// 玩家看到「钠的替代选项菜单」只会更懵。模组名在社区里就是英文，谁也不会说「我装了钠模组」。
/// </para>
/// <para>
/// 做法是把这些词换成 <c>[[T1]]</c> 占位符再送翻译 —— 2026-10-29 实测 MyMemory
/// 会原样保留这种方括号标记，译文回来后再换回去。
/// </para>
/// </remarks>
public sealed record TranslationRequest(string Text, IReadOnlyList<string> KeepAsIs)
{
    /// <summary>
    /// 按项目名自动推出「不准翻」的名单。
    /// </summary>
    /// <param name="description">要翻的简介。</param>
    /// <param name="projectTitle">项目名（搜索结果的 Title）。</param>
    /// <remarks>
    /// <para>
    /// 名单有两个来源：
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <b>项目名本身</b>，以及它拆开的每个词 —— 项目叫 <c>Sodium Extra</c>，
    ///     简介里只提到 <c>Sodium</c> 时也得保护住。
    ///   </item>
    ///   <item>
    ///     <b>内置的 MC 生态词</b>（Minecraft、Fabric、Forge…）—— 这些被翻成
    ///     「织物」「锻造」同样是灾难，而且几乎每篇简介都会出现。
    ///   </item>
    /// </list>
    /// </remarks>
    public static TranslationRequest Create(string description, string? projectTitle)
    {
        var keep = new List<string>();

        if (!string.IsNullOrWhiteSpace(projectTitle))
        {
            keep.Add(projectTitle.Trim());

            // 拆词：Sodium Extra → Sodium / Extra，Quests-Addon → Quests / Addon
            foreach (var piece in projectTitle.Split(
                         new[] { ' ', '-', '_', '\'', ':', '/', '+' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (piece.Length < 3) continue;

                // 项目名里的泛用英文词不保护。
                // 实测教训：Sodium Shadowy Path Blocks 拆出来的 Blocks 被保护了，
                // 简介里的 "non-full blocks" 就翻成「非完整的Blocks」，比不翻还难读。
                if (TooGeneric.Contains(piece) && !LooksAbbreviated(piece)) continue;

                keep.Add(piece);
            }
        }

        foreach (var word in EcosystemWords)
        {
            if (!keep.Contains(word, StringComparer.OrdinalIgnoreCase))
                keep.Add(word);
        }

        return new TranslationRequest(description, keep);
    }

    /// <summary>
    /// 是不是缩写形态（全大写 / 含数字 / 驼峰）。
    /// </summary>
    /// <remarks>
    /// 这个判断用来给泛用词开例外：<c>API</c> 在黑名单里，但它是全大写，
    /// 明显是缩写而不是普通单词，该保护。
    /// </remarks>
    private static bool LooksAbbreviated(string word)
    {
        if (word.Length <= 1) return false;

        // 全大写：API、SSPB、JEI
        if (word.All(c => !char.IsLetter(c) || char.IsUpper(c))) return true;

        // 含数字：TConstruct、1_20_1
        if (word.Any(char.IsDigit)) return true;

        // 驼峰：NeoForge、OptiFine
        return word.Skip(1).Any(char.IsUpper);
    }

    /// <summary>
    /// 项目名里出现频率极高、但本身没有品牌含义的英文词。
    /// </summary>
    /// <remarks>
    /// 这些词不需要保护 —— 翻成中文反而更好读（<c>Options</c> → 选项、<c>Blocks</c> → 方块）。
    /// 名单只收「模组命名常用词」，不要往里塞任何具体模组名。
    /// </remarks>
    private static readonly HashSet<string> TooGeneric = new(StringComparer.OrdinalIgnoreCase)
    {
        // 界面相关
        "Options", "Option", "Menu", "Menus", "Gui", "Hud", "Screen", "Screens",
        "Config", "Configs", "Settings", "Settings",
        // 内容单位
        "Blocks", "Block", "Items", "Item", "Entities", "Entity", "Mobs", "Mob",
        "Tools", "Tool", "Utils", "Utility", "Utilities", "Packs", "Pack",
        "Mods", "Mod", "Addon", "Addons", "Plugin", "Plugins",
        "Books", "Book", "Maps", "Map", "Skins", "Skin", "Sounds", "Sound",
        "Music", "Textures", "Texture", "Models", "Model", "Fonts", "Font",
        // 效果与风格
        "Light", "Lights", "Lighting", "Smooth", "Smoothing", "Shadow", "Shadows",
        "Shadowy", "Render", "Rendering", "Engine", "Shaders", "Shader", "Water",
        "Grass", "Stone", "Wood", "Food", "Combat", "Magic", "Tech", "Technology",
        "Storage", "Quest", "Quests", "Animation", "Animations", "Sound",
        // 冠词式后缀
        "Extra", "Plus", "Pro", "Lite", "Mini", "Max", "Ultra", "Core", "Data",
        "Overhaul", "Revamped", "Reforged", "Reloaded", "Remastered",
        "Classic", "Simple", "Better", "Best", "Modern", "Advanced",
        "Fast", "Faster", "Quick", "Slow", "Easy", "Hard", "Auto", "Smart",
        "New", "Old", "World", "Worlds", "Level", "Levels", "Game", "Games",
        "Player", "Players", "Client", "Server", "Multi", "Single",
        "Path", "Paths", "Craft", "Crafting",
    };

    /// <summary>
    /// MC 生态里固定不翻的词。
    /// </summary>
    /// <remarks>
    /// 只收「翻了必然错」的：加载器名、平台名、通用缩写。
    /// <b>不要往这里加具体模组名</b> —— 那是项目名的活儿，加不完。
    /// </remarks>
    private static readonly string[] EcosystemWords =
    {
        "Minecraft", "Mojang",
        "Fabric", "Forge", "NeoForge", "Quilt",
        "OptiFine", "Iris", "Modrinth", "CurseForge",
        "Java",
        // 缩写类是机翻最爱乱翻的：API 被翻成「应用程序接口」，FPS 被翻成「每秒帧数」
        "API", "FPS", "TPS", "GUI", "HUD", "PBR", "NBT",
        "SMP", "PvP", "PvE", "RPG", "AFK",
        "JEI", "REI", "EMI",
    };
}
