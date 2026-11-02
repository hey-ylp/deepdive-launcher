using System.Text;
using System.Text.RegularExpressions;

namespace DeepDive.Core.Translation;

/// <summary>
/// 项目名的中英对照：英文标题 → 中文名，中文关键词 → 英文检索词。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么标题不送机翻</b>：机翻整串名字会毁掉品牌词。实测把
/// <c>Alternative Options Menu for Sodium</c> 送进 MyMemory，回来的是
/// 「钠的替代选项菜单」；<c>Create</c> 会被翻成「创造」，<c>Iris</c> 变成彩虹女神。
/// 玩家看到「创造」根本不知道指的是哪个模组。
/// </para>
/// <para>
/// 所以这里走<b>词表拼接</b>：认得出来的通用词（Shaders → 光影、Optimization → 优化）
/// 翻成中文，认不出来的（Sodium、Create、Iris）原样留着当品牌词。
/// 结果就是 PCL 那种「<c>Iris 光影</c>」「<c>Sodium 阴影路径方块</c>」的中英混排。
/// </para>
/// <para>
/// <b>词表是封闭集合</b>，不联网、不耗翻译额度，一次搜索 20 条瞬间就能算完 ——
/// 这是它相对机翻最大的好处，也是必须做得克制的原因：只在有把握的时候翻，
/// 一个词都认不出来就返回 <c>null</c>，界面老老实实只显示英文。
/// </para>
/// </remarks>
public static class NameLocalizer
{
    /// <summary>英文单词（含 0-9 和撇号，照顾 <c>Steam 'n' Rails</c> 这种）。</summary>
    private static readonly Regex WordPattern = new("[A-Za-z][A-Za-z0-9']*", RegexOptions.Compiled);

    private static readonly Regex CjkPattern = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

    /// <summary>
    /// 中文不需要的虚词，遇到直接丢掉。
    /// </summary>
    /// <remarks>
    /// 不丢的话会出现「更好的 的 战斗」这种。中文里这些词根本没有对应物。
    /// </remarks>
    private static readonly HashSet<string> FunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "for", "and", "or", "in", "on", "at", "to", "with",
        "by", "from", "into", "your", "my", "our", "its", "is", "are", "be",
    };

    /// <summary>
    /// 英文通用词 → 中文。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只收通用词，绝不收品牌名。</b> 往这张表里塞 <c>create</c>、<c>iris</c>、
    /// <c>sodium</c> 这种，标题就会被翻成「创造」「虹膜」「钠」—— 社区里没人这么叫。
    /// </para>
    /// <para>
    /// 单词变形（复数 s/es、-ing）由 <see cref="Lookup"/> 兜底，不用在这里把
    /// <c>block</c> / <c>blocks</c> 两种都写一遍；但高频复数写上来更稳（少一次试探）。
    /// </para>
    /// </remarks>
    private static readonly (string En, string Zh)[] Pairs =
    {
        // ── 类型与结构 ────────────────────────────────
        ("mod", "模组"), ("mods", "模组"), ("modpack", "整合包"), ("modpacks", "整合包"),
        ("addon", "附加"), ("addons", "附加"), ("plugin", "插件"), ("plugins", "插件"),
        ("datapack", "数据包"), ("datapacks", "数据包"),
        ("pack", "包"), ("packs", "包"), ("resource", "资源"), ("resources", "资源"),
        ("library", "前置库"), ("framework", "框架"), ("core", "核心"), ("base", "基础"),
        ("essentials", "基础"), ("foundation", "底层"), ("toolkit", "工具集"),
        ("utility", "实用工具"), ("utilities", "实用工具"), ("util", "工具"),
        ("tool", "工具"), ("tools", "工具"), ("tweak", "微调"), ("tweaks", "微调"),
        ("fix", "修复"), ("fixes", "修复"), ("patch", "补丁"), ("patches", "补丁"),
        ("update", "更新"), ("updates", "更新"), ("upgrade", "升级"),
        ("expansion", "扩展"), ("extension", "扩展"), ("extensions", "扩展"),
        ("compat", "兼容"), ("compatibility", "兼容"), ("integrations", "联动"),
        ("integration", "联动"), ("bridge", "桥接"), ("adapter", "适配"),
        ("loader", "加载器"), ("installer", "安装器"), ("manager", "管理器"),
        ("editor", "编辑器"), ("viewer", "查看器"), ("browser", "浏览器"),

        // ── 渲染与画面 ────────────────────────────────
        ("shader", "光影"), ("shaders", "光影"), ("shaderpack", "光影包"), ("shaderpacks", "光影包"),
        ("render", "渲染"), ("rendering", "渲染"), ("graphics", "画面"),
        ("visual", "视觉"), ("visuals", "视觉效果"), ("visualsplus", "视觉效果增强"),
        ("lighting", "光照"), ("light", "光照"), ("lights", "光源"),
        ("shadow", "阴影"), ("shadows", "阴影"), ("shadowy", "阴影"),
        ("bloom", "泛光"), ("reflection", "反射"), ("reflections", "反射"),
        ("fog", "雾"), ("sky", "天空"), ("cloud", "云"), ("clouds", "云"),
        ("water", "水"), ("particle", "粒子"), ("particles", "粒子"),
        ("texture", "材质"), ("textures", "材质"), ("model", "模型"), ("models", "模型"),
        ("realistic", "写实"), ("cartoon", "卡通"), ("anime", "动漫"),
        ("retro", "复古"), ("pixel", "像素"), ("pbr", "PBR"),

        // ── 世界与生成 ────────────────────────────────
        ("world", "世界"), ("worlds", "世界"), ("worldgen", "世界生成"),
        ("generation", "生成"), ("generator", "生成器"), ("biome", "生物群系"), ("biomes", "生物群系"),
        ("dimension", "维度"), ("dimensions", "维度"), ("nether", "下界"), ("overworld", "主世界"),
        ("structure", "结构"), ("structures", "结构"), ("dungeon", "地牢"), ("dungeons", "地牢"),
        ("cave", "洞穴"), ("caves", "洞穴"), ("ocean", "海洋"), ("oceans", "海洋"),
        ("weather", "天气"), ("season", "季节"), ("seasons", "季节"),
        ("tree", "树木"), ("trees", "树木"), ("foliage", "植被"), ("leaves", "树叶"),
        ("crop", "作物"), ("crops", "作物"), ("terrain", "地形"), ("chunk", "区块"),
        ("map", "地图"), ("maps", "地图"), ("minimap", "小地图"), ("waypoint", "路径点"),
        ("waypoints", "路径点"), ("coordinate", "坐标"), ("coordinates", "坐标"),
        ("portal", "传送门"), ("teleport", "传送"), ("spawn", "生成点"),

        // ── 玩法 ─────────────────────────────────────
        ("adventure", "冒险"), ("survival", "生存"), ("creative", "创造"),
        ("hardcore", "极限"), ("combat", "战斗"), ("magic", "魔法"),
        ("tech", "科技"), ("technology", "科技"), ("technical", "技术"),
        ("storage", "存储"), ("quest", "任务"), ("quests", "任务"),
        ("achievement", "成就"), ("achievements", "成就"),
        ("advancement", "进度"), ("advancements", "进度"),
        ("boss", "首领"), ("bosses", "首领"), ("enemy", "敌人"), ("enemies", "敌人"),
        ("difficulty", "难度"), ("parkour", "跑酷"), ("puzzle", "解谜"), ("puzzles", "解谜"),
        ("minigame", "小游戏"), ("minigames", "小游戏"), ("economy", "经济"),
        ("trade", "交易"), ("trading", "交易"), ("shop", "商店"), ("market", "市场"),
        ("farming", "农业"), ("farm", "农场"), ("food", "食物"), ("cooking", "烹饪"),
        ("recipe", "配方"), ("recipes", "配方"), ("crafting", "合成"),
        ("enchant", "附魔"), ("enchanting", "附魔"), ("enchantment", "附魔"), ("enchantments", "附魔"),
        ("potion", "药水"), ("potions", "药水"), ("effect", "效果"), ("effects", "效果"),
        ("skill", "技能"), ("skills", "技能"), ("leveling", "等级"), ("level", "等级"),
        ("damage", "伤害"), ("health", "生命"), ("hunger", "饥饿"),
        ("armor", "盔甲"), ("armour", "盔甲"), ("weapon", "武器"), ("weapons", "武器"),
        ("sword", "剑"), ("swords", "剑"), ("bow", "弓"), ("shield", "盾"),
        ("gun", "枪械"), ("guns", "枪械"), ("machine", "机器"), ("machines", "机器"),
        ("automation", "自动化"), ("energy", "能源"), ("power", "能量"),
        ("industry", "工业"), ("industrial", "工业"), ("mining", "挖矿"), ("mine", "矿洞"),
        ("ore", "矿石"), ("ores", "矿石"), ("gem", "宝石"), ("gems", "宝石"),
        ("smithing", "锻造"), ("durability", "耐久"), ("inventory", "物品栏"),
        ("container", "容器"), ("containers", "容器"), ("chest", "箱子"), ("furnace", "熔炉"),
        ("backpack", "背包"), ("backpacks", "背包"), ("slot", "槽位"), ("slots", "槽位"),
        ("hotbar", "快捷栏"), ("item", "物品"), ("items", "物品"),
        ("block", "方块"), ("blocks", "方块"), ("entity", "实体"), ("entities", "实体"),
        ("mob", "生物"), ("mobs", "生物"), ("animal", "动物"), ("animals", "动物"),
        ("villager", "村民"), ("villagers", "村民"), ("pet", "宠物"), ("pets", "宠物"),
        ("dragon", "龙"), ("dragons", "龙"), ("fishing", "钓鱼"), ("fish", "鱼"),
        ("furniture", "家具"), ("decoration", "装饰"), ("decorative", "装饰"),
        ("building", "建筑"), ("blockset", "方块集"), ("rotation", "旋转"),
        ("camera", "相机"), ("screenshot", "截图"), ("zoom", "缩放"),
        ("culling", "剔除"), ("network", "网络"), ("feature", "特性"), ("features", "特性"),
        ("animation", "动画"), ("animations", "动画"), ("modeling", "建模"),
        ("path", "路径"), ("paths", "路径"), ("road", "道路"), ("roads", "道路"),

        // ── 界面与体验 ────────────────────────────────
        ("client", "客户端"), ("server", "服务端"), ("multiplayer", "多人"), ("singleplayer", "单人"),
        ("chat", "聊天"), ("voice", "语音"), ("skin", "皮肤"), ("skins", "皮肤"),
        ("cape", "披风"), ("name", "名称"), ("description", "描述"),
        ("tooltip", "提示框"), ("tooltips", "提示框"), ("tag", "标签"), ("tags", "标签"),
        ("filter", "筛选"), ("sorting", "排序"), ("search", "搜索"),
        ("quality", "画质"), ("performance", "性能"), ("optimization", "优化"),
        ("optimized", "优化"), ("optimizations", "优化"), ("fps", "帧率"),
        ("video", "视频"), ("audio", "音频"), ("sound", "音效"), ("sounds", "音效"),
        ("music", "音乐"), ("language", "语言"), ("languages", "语言"), ("translation", "翻译"),
        ("smooth", "平滑"), ("smoothing", "平滑"), ("loading", "加载"),
        ("startup", "启动"), ("boot", "启动"), ("crash", "崩溃"),
        ("debug", "调试"), ("log", "日志"), ("logs", "日志"),
        ("diagnostics", "诊断"), ("profiler", "性能分析"), ("benchmark", "基准测试"),
        ("sync", "同步"), ("backup", "备份"), ("backups", "备份"),
        ("save", "存档"), ("saves", "存档"), ("auto", "自动"), ("automatic", "自动"),
        ("manual", "手动"), ("quick", "快速"), ("fast", "快速"), ("slow", "慢速"),
        ("time", "时间"), ("clock", "时钟"), ("timer", "计时器"), ("calendar", "日历"),
        ("journal", "日志"), ("notebook", "笔记"), ("book", "书"), ("books", "书"),
        ("guide", "指南"), ("tutorial", "教程"), ("wiki", "百科"),
        ("info", "信息"), ("information", "信息"), ("config", "配置"), ("configs", "配置"),
        ("option", "选项"), ("options", "选项"), ("setting", "设置"), ("settings", "设置"),
        ("menu", "菜单"), ("menus", "菜单"), ("screen", "界面"), ("screens", "界面"),
        ("gui", "界面"), ("hud", "HUD"), ("interface", "界面"),

        // ── 风格与程度 ────────────────────────────────
        ("classic", "经典"), ("legacy", "传承"), ("modern", "现代"), ("advanced", "高级"),
        ("simple", "简易"), ("better", "更好的"), ("improved", "改进"), ("enhanced", "增强"),
        ("extended", "拓展"), ("ultimate", "终极"), ("complete", "完整"),
        ("overhaul", "大改"), ("reforged", "重铸"), ("reloaded", "重制"),
        ("remastered", "重制"), ("revamped", "翻新"), ("remake", "重制"),
        ("lite", "轻量"), ("mini", "迷你"), ("plus", "增强"), ("extra", "附加"),
        ("small", "小型"), ("tiny", "微型"), ("huge", "巨型"),
        ("random", "随机"), ("custom", "自定义"), ("vanilla", "原版"),
        ("cute", "可爱"), ("funny", "搞怪"), ("spooky", "阴森"), ("horror", "恐怖"),
        ("halloween", "万圣节"), ("christmas", "圣诞"),
        ("winter", "冬季"), ("summer", "夏季"), ("autumn", "秋季"), ("spring", "春季"),
        ("medieval", "中世纪"), ("futuristic", "未来"), ("space", "太空"),
        ("nuclear", "核能"), ("ancient", "远古"), ("mystic", "神秘"), ("mystical", "神秘"),
        ("fantasy", "奇幻"), ("realism", "写实"), ("immersive", "沉浸"),
        ("just", "仅"), ("enough", "足够的"), ("more", "更多"), ("less", "更少"),
        ("all", "全部"), ("every", "每个"), ("any", "任意"), ("some", "一些"),
        ("no", "无"), ("not", "不"), ("non", "非"), ("unlimited", "无限"),
        ("infinite", "无限"), ("compact", "紧凑"), ("dense", "密集"), ("distant", "远景"),
        ("far", "远景"), ("near", "近景"), ("bright", "明亮"), ("dark", "暗色"),
        ("colorful", "缤纷"), ("colored", "彩色"), ("coloured", "彩色"),
        ("dynamic", "动态"), ("static", "静态"), ("smoothness", "平滑度"),
    };

    /// <summary>
    /// 社区既定译名（按 Modrinth 的 slug 认）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要单列一张表</b>：词表能拼出「Iris 光影」这种，但拼不出「钠」——
    /// <c>Sodium</c> 是单个品牌词，词表一个都认不出来，只能显示英文。
    /// 而中文社区确实管它叫「钠」。这类「公认叫法」只能人工收录，没有规律可推。
    /// </para>
    /// <para>
    /// 收录标准很严：<b>只有确定的中文社区叫法才写进来</b>。拿不准的一律不写，
    /// 让它走词表或者留在英文 —— 编一个错误的中文名比显示英文伤害大得多。
    /// </para>
    /// </remarks>
    private static readonly (string Key, string Zh)[] KnownNames =
    {
        ("sodium", "钠"),
        ("lithium", "锂"),
        ("krypton", "氪"),
        ("phosphor", "磷光"),
        ("indium", "铟"),
        ("starlight", "星光"),
        ("sodium-extra", "钠：扩展"),
        ("appleskin", "苹果皮"),
        ("create", "机械动力"),
        ("jei", "JEI 物品管理器"),
        ("journeymap", "旅行地图"),
        ("xaeros-minimap", "Xaero 小地图"),
        ("xaeros-world-map", "Xaero 世界地图"),
        ("optifine", "高清修复"),
        ("iris", "虹膜光影"),
        ("distant-horizons", "远景视距"),
        ("modmenu", "模组菜单"),
        ("modernfix", "现代修复"),
    };

    /// <summary>词表：小写英文 → 中文。</summary>
    private static readonly Dictionary<string, string> Words = BuildWords();

    /// <summary>已知译名：slug / 标题（小写、空格换连字符）→ 中文。</summary>
    private static readonly Dictionary<string, string> Known = BuildKnown();

    /// <summary>
    /// 社区惯用中文叫法 → Modrinth 英文检索词。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这张表解决的是「知道中文名但搜不到」：搜「机械动力」在 Modrinth 上一条都不会有，
    /// 得换成 <c>create</c>。同理「钠」要换成 <c>sodium</c>。
    /// </para>
    /// <para>
    /// 收录标准是「中文社区确实这么叫，且英文词独一无二」。像「更好的」这种前缀
    /// 绝不能收 —— <c>better</c> 一搜出来几千个，等于没搜。
    /// </para>
    /// </remarks>
    private static readonly (string Zh, string En)[] Aliases =
    {
        ("钠", "sodium"), ("锂", "lithium"), ("铷", "rubidium"), ("磷", "phosphor"),
        ("机械动力", "create"), ("暮色森林", "twilight forest"), ("匠魂", "tinkers construct"),
        ("神秘时代", "thaumcraft"), ("通用机械", "mekanism"), ("应用能源", "applied energistics"),
        ("植物魔法", "botania"), ("热力膨胀", "thermal expansion"), ("沉浸工程", "immersive engineering"),
        ("虚无世界", "advent of ascension"), ("拔刀剑", "slashblade"), ("女仆", "little maid"),
        ("苹果皮", "appleskin"), ("小地图", "minimap"), ("旅行地图", "journeymap"),
        ("光影包", "shaders"), ("光影", "shaders"), ("材质包", "resource packs"),
        ("资源包", "resource packs"), ("整合包", "modpacks"), ("数据包", "datapacks"),
        ("模组菜单", "mod menu"), ("物品管理器", "just enough items"), ("配方查询", "jei"),
        ("漏斗", "hopper"), ("区块加载", "chunk loading"), ("世界生成", "worldgen"),
        ("生物群系", "biomes"), ("下界", "nether"), ("末地", "end dimensions"),
        ("物品栏", "inventory"), ("合成表", "recipe"), ("自动农场", "auto farm"),
        ("高清修复", "optifine"), ("星光", "starlight"), ("氪", "krypton"), ("铟", "indium"),
        ("虹膜光影", "iris shaders"), ("远景视距", "distant horizons"), ("现代修复", "modernfix"),
    };

    /// <summary>
    /// 反查表：中文 → 英文检索词。词表倒过来，外加社区俗称。
    /// </summary>
    /// <remarks>
    /// <b>声明位置必须在 <see cref="Aliases"/> 后面</b> —— C# 的静态字段按书写顺序初始化，
    /// 放前面的话 <see cref="BuildReverse"/> 跑起来时别名表还是 <c>null</c>，
    /// 启动就崩在类型初始化上。
    /// </remarks>
    private static readonly Dictionary<string, string> Reverse = BuildReverse();

    // ── 对外 ────────────────────────────────────────────

    /// <summary>
    /// 英文项目名 → 中文名。认不出来返回 <c>null</c>（界面就只显示英文）。
    /// </summary>
    /// <param name="title">英文原名。</param>
    /// <param name="slug">Modrinth 的 slug，给了就先查社区既定译名。</param>
    /// <remarks>
    /// <para>
    /// 三条门槛，都是为了「宁可不翻，也不出错」：
    /// </para>
    /// <list type="number">
    ///   <item>slug 命中既定译名 → 直接用它（「Sodium」→「钠」，这是词表拼不出来的）。</item>
    ///   <item>一个词都没命中词表 → <c>null</c>。<c>Terralith</c> 这种纯品牌名就该保持原样。</item>
    ///   <item>命中率不到一半 → <c>null</c>。五个词只认出「模组」一个，
    ///         拼出来是「Some Random 模组 名称」，比英文更难读。</item>
    /// </list>
    /// </remarks>
    public static string? ChineseTitle(string? title, string? slug = null)
    {
        if (!string.IsNullOrWhiteSpace(slug) && Known.TryGetValue(slug.Trim(), out var knownTitle))
            return knownTitle;

        if (string.IsNullOrWhiteSpace(title)) return null;

        // 标题本身也可能是既定译名的键（「Mod Menu」这类 slug 和标题对不上的情况）
        if (Known.TryGetValue(TitleKey(title), out knownTitle)) return knownTitle;

        var matches = WordPattern.Matches(title);
        if (matches.Count == 0) return null;

        var builder = new StringBuilder(title.Length + 8);
        var total = 0;      // 参与判断的词数（虚词不算）
        var translated = 0; // 命中词表的词数
        var lastWasChinese = false;

        foreach (Match match in matches)
        {
            var word = match.Value;

            // 虚词直接扔，中文没有对应物
            if (FunctionWords.Contains(word)) continue;

            total++;

            string piece;
            bool isChinese;

            if (KeepAsIs(word))
            {
                piece = word;
                isChinese = false;
            }
            else if (Lookup(word) is { } chinese)
            {
                piece = chinese;
                isChinese = true;
                translated++;
            }
            else
            {
                piece = word;
                isChinese = false;
            }

            // 中文之间不加空格（「阴影路径方块」），中英之间加（「Sodium 阴影路径方块」）
            if (builder.Length > 0 && !(isChinese && lastWasChinese))
                builder.Append(' ');

            builder.Append(piece);
            lastWasChinese = isChinese;
        }

        if (translated == 0) return null;

        // 命中率过低就别硬凑：五个词只认出一个，拼出来是半英半中的四不像
        if (translated * 2 < total) return null;

        var result = builder.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    /// <summary>这段文本里有没有汉字。</summary>
    public static bool HasChinese(string? text) =>
        !string.IsNullOrWhiteSpace(text) && CjkPattern.IsMatch(text);

    /// <summary>
    /// 中文关键词 → 英文检索词。全是英文就原样返回；
    /// 有汉字但词表覆盖不全时返回 <c>null</c>（调用方再走机翻）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用<b>最长匹配</b>扫一遍：「机械动力」必须先撞上四字别名，不能被「动力」之类切走。
    /// </para>
    /// <para>
    /// 覆盖不全就整体放弃 —— 半句中文送进 Modrinth 只会搜出零条，
    /// 那比不搜还让人困惑。
    /// </para>
    /// </remarks>
    public static string? EnglishQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return query;

        var text = query.Trim();
        if (!HasChinese(text)) return text;

        var builder = new StringBuilder(text.Length * 2);
        var chineseChars = 0;
        var coveredChars = 0;
        var index = 0;

        while (index < text.Length)
        {
            if (!CjkPattern.IsMatch(text[index].ToString()))
            {
                // 英文/数字/空格原样带过去（「sodium 优化」这种混排很常见）
                builder.Append(text[index]);
                index++;
                continue;
            }

            chineseChars++;

            var matched = false;

            // 最长的中文别名也就 6 个字，从长往短试
            for (var length = Math.Min(6, text.Length - index); length >= 1; length--)
            {
                var slice = text.Substring(index, length);
                if (!Reverse.TryGetValue(slice, out var english)) continue;

                if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
                builder.Append(english).Append(' ');

                index += length;
                coveredChars += length;
                matched = true;
                break;
            }

            if (!matched) index++;
        }

        // 覆盖率不到 100% 就别用了：剩下那半截中文注定搜不到东西
        if (chineseChars == 0 || coveredChars < chineseChars) return null;

        var result = Regex.Replace(builder.ToString(), @"\s{2,}", " ").Trim();
        return result.Length == 0 ? null : result;
    }

    // ── 内部 ────────────────────────────────────────────

    /// <summary>
    /// 把机翻出来的整句压成几个实词，当检索词用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 实测：「能自动种地的模组」机翻回来是
    /// <c>Module capable of automatic farming</c> —— 句子没错，但拿去搜 Modrinth
    /// 会把 capable / of 这些词也算进相关性，结果被拉偏。
    /// </para>
    /// <para>
    /// 这里只做两件事：扔掉功能词，最多留 4 个实词。
    /// <c>Module capable of automatic farming</c> → <c>Module automatic farming</c>。
    /// </para>
    /// </remarks>
    public static string? TrimToKeywords(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return null;

        var keep = phrase
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '(', ')', '"' },
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !KeywordNoise.Contains(word))
            .Take(4)
            .ToList();

        return keep.Count == 0 ? null : string.Join(" ", keep);
    }

    /// <summary>检索词里没有信息量的词（功能词 + 机翻常见的废话）。</summary>
    private static readonly HashSet<string> KeywordNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "of", "for", "with", "and", "or", "to", "in", "on", "at", "by",
        "from", "as", "is", "are", "be", "that", "which", "who", "can", "could", "able",
        "capable", "allow", "allows", "able", "using", "use", "this", "these", "those",
        "it", "its", "their", "you", "your", "yourself", "when", "where", "while", "into",
        "makes", "make", "made", "give", "gives", "adds", "add", "based",
    };

    /// <summary>
    /// 这个词要不要原样保留（当作品牌词）。
    /// </summary>
    /// <remarks>
    /// 全大写（<c>API</c>、<c>JEI</c>）、含数字（<c>TConstruct</c>）、
    /// 中间有大写（<c>NeoForge</c>、<c>OptiFine</c>）—— 这三种形态几乎必然是名字而非普通单词。
    /// 单个字母（<c>'n'</c> 里的 <c>n</c>）也留着，翻不了。
    /// </remarks>
    private static bool KeepAsIs(string word)
    {
        if (word.Length <= 1) return true;
        if (word.Any(char.IsDigit)) return true;
        if (word.All(c => !char.IsLetter(c) || char.IsUpper(c))) return true;
        return word.Skip(1).Any(char.IsUpper);
    }

    /// <summary>查词表，顺手试一下常见变形。</summary>
    private static string? Lookup(string word)
    {
        var lower = word.ToLowerInvariant();
        if (Words.TryGetValue(lower, out var zh)) return zh;

        // 复数 / 分词，词表里只写原形就行
        if (lower.EndsWith("es", StringComparison.Ordinal) &&
            Words.TryGetValue(lower[..^2], out zh)) return zh;

        if (lower.Length > 3 && lower.EndsWith('s') &&
            Words.TryGetValue(lower[..^1], out zh)) return zh;

        if (lower.EndsWith("ing", StringComparison.Ordinal) &&
            Words.TryGetValue(lower[..^3], out zh)) return zh;

        return null;
    }

    private static Dictionary<string, string> BuildWords()
    {
        var map = new Dictionary<string, string>(Pairs.Length, StringComparer.OrdinalIgnoreCase);

        // 后面的条目覆盖前面的 —— 想让某个词换个说法，在数组末尾再写一遍即可
        foreach (var (en, zh) in Pairs)
            map[en] = zh;

        return map;
    }

    private static Dictionary<string, string> BuildKnown()
    {
        var map = new Dictionary<string, string>(KnownNames.Length, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, zh) in KnownNames)
            map[key] = zh;

        return map;
    }

    /// <summary>标题归一化成 slug 的样子：<c>Mod Menu</c> → <c>mod-menu</c>。</summary>
    private static string TitleKey(string title) =>
        title.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');

    /// <summary>
    /// 倒表。别名优先于词表（「机械动力」是 <c>create</c> 而不是「动力」拆开）。
    /// </summary>
    private static Dictionary<string, string> BuildReverse()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (zh, en) in Aliases)
            map[zh] = en;

        // 同一个中文可能对应多个英文（storage 和 storage 不冲突，但 shader/shaderpack 不同），
        // 先到先得，保持词表里的顺序
        foreach (var (en, zh) in Pairs)
        {
            if (!map.ContainsKey(zh))
                map[zh] = en;
        }

        return map;
    }
}
