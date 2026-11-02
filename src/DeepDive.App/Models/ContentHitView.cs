using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DeepDive.Core.Modrinth;
using DeepDive.Core.Translation;

namespace DeepDive.App.Models;

/// <summary>一条搜索结果和当前所选版本的关系。</summary>
public enum CompatState
{
    /// <summary>还没选游戏版本，没法判断。</summary>
    Unknown,

    /// <summary>支持当前选中的游戏版本。</summary>
    Compatible,

    /// <summary>不支持当前选中的版本。</summary>
    Incompatible,
}

/// <summary>
/// 搜索结果列表里的一项（展示用）。
/// </summary>
/// <remarks>
/// <para>
/// 这一层的存在只有一个理由：<b>把「版本 + 加载器」翻译成人能一眼看懂的话</b>。
/// </para>
/// <para>
/// Modrinth 的返回值里，<c>versions</c> 是这个项目支持的全部游戏版本（Sodium 有 41 个），
/// <c>categories</c> 是加载器和功能分类混在一起的数组。原样丢到界面上没人看得懂，
/// 所以这里算三件事：
/// </para>
/// <list type="number">
///   <item>支持不支持<b>当前选的这个版本</b> —— 一条徽章说清；</item>
///   <item>不支持的话，它最高支持到哪 —— 用发布时间排过序的序号表算，不能按字符串比；</item>
///   <item>支持哪些<b>加载器</b> —— 拿白名单从 categories 里捞，功能分类不算。</item>
/// </list>
/// <para>
/// 实现 <see cref="INotifyPropertyChanged"/> 只为一个原因：<b>简介的中文译文是后到的</b>。
/// 搜索结果先出英文列表，译文在后台一条条补，补进来时得让界面知道该重绘哪一行。
/// </para>
/// </remarks>
public sealed class ContentHitView : INotifyPropertyChanged
{
    private static readonly Dictionary<ContentType, string> TypeColors = new()
    {
        [ContentType.Mod] = "#4C6EF5",
        [ContentType.Modpack] = "#E8590C",
        [ContentType.Shader] = "#9C36B5",
        [ContentType.ResourcePack] = "#0CA678",
    };

    public ContentHitView(
        ModrinthSearchHit hit,
        ContentTypeInfo typeInfo,
        string? currentGameVersion,
        IReadOnlyDictionary<string, int> versionRank)
    {
        Hit = hit;
        TypeInfo = typeInfo;

        Title = string.IsNullOrWhiteSpace(hit.Title) ? hit.Slug : hit.Title;
        TitleZh = NameLocalizer.ChineseTitle(Title, hit.Slug) ?? string.Empty;
        Author = hit.Author;
        Description = hit.Description;
        Slug = hit.Slug;
        ProjectId = hit.ProjectId;

        TypeLabel = typeInfo.DisplayName;
        TypeColor = TypeColors.TryGetValue(typeInfo.Type, out var color) ? color : "#4C6EF5";

        Loaders = PickLoaders(hit, typeInfo);
        HasLoaderInfo = Loaders.Count > 0;

        // 资源包本来就没有「加载器」这回事（原版直接能读），写「未标加载器」等于
        // 告诉用户数据不全，会让他反复确认。这类干脆不显示。
        LoaderText = typeInfo.Type == ContentType.ResourcePack
            ? string.Empty
            : HasLoaderInfo
                ? string.Join(" · ", Loaders)
                : "未标加载器";

        DownloadsLabel = FormatCount(hit.Downloads);
        FollowsLabel = FormatCount(hit.Follows);
        UpdatedLabel = hit.DateModified == default
            ? "—"
            : hit.DateModified.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        Initial = FirstChar(Title);
        AccentColor = ToHex(hit.Color) ?? TypeColor;

        Categories = CategoryLocalizer.Functional(hit.Categories, typeInfo);
        CategoryText = string.Join(" · ", Categories);

        (Compat, VersionBadge, VersionTip) = DescribeVersion(hit.Versions, currentGameVersion, versionRank);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 简介的中文译文。<b>后到的</b> —— 搜索一完成界面先显示英文，
    /// 译文翻好了再填进来。空字符串表示「还没有译文」，界面就显示英文原文。
    /// </summary>
    public string DescriptionZh
    {
        get => _descriptionZh;
        private set
        {
            if (_descriptionZh == value) return;

            _descriptionZh = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDescriptionZh));
            OnPropertyChanged(nameof(DescriptionLine));
        }
    }

    private string _descriptionZh = string.Empty;

    /// <summary>有没有中文译文。界面靠它决定显示「中文 + 原文」还是「只有原文」。</summary>
    public bool HasDescriptionZh => _descriptionZh.Length > 0;

    /// <summary>正文那一行：有译文用译文，没有就用英文原文。</summary>
    public string DescriptionLine => HasDescriptionZh ? _descriptionZh : Description;

    /// <summary>
    /// 项目图标。<b>后到的</b> —— 列表先用首字母色块顶上，图片下载并解码完再换过来。
    /// </summary>
    /// <remarks>
    /// 为 <c>null</c> 有三种情况：还没下完、这个项目没传图标、这台机器解不了 webp。
    /// 三种对界面来说都是同一件事 —— 继续用色块，不报错。
    /// </remarks>
    public ImageSource? Icon
    {
        get => _icon;
        internal set
        {
            if (ReferenceEquals(_icon, value)) return;

            _icon = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    private ImageSource? _icon;

    /// <summary>有没有真图标。没有就显示首字母色块。</summary>
    public bool HasIcon => _icon is not null;

    /// <summary>填译文。翻好了由视图模型调。</summary>
    public void ApplyTranslation(string translation)
    {
        if (string.IsNullOrWhiteSpace(translation)) return;
        DescriptionZh = translation;
    }

    /// <summary>丢掉译文，退回显示英文原文。用户关掉「中文简介」时用。</summary>
    public void ClearTranslation() => DescriptionZh = string.Empty;

    /// <summary>功能分类的中文名（最多 3 个）。加载器不在这里面，它单独显示。</summary>
    public IReadOnlyList<string> Categories { get; }

    /// <summary>分类拼成一行：<c>优化 · 装饰</c>。一个都没有时为空。</summary>
    public string CategoryText { get; }

    public bool HasCategories => Categories.Count > 0;

    /// <summary>分类的完整解释（ToolTip）。</summary>
    public string CategoryTip =>
        Categories.Count == 0 ? string.Empty : $"分类：{string.Join("、", Categories)}";

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ModrinthSearchHit Hit { get; }

    public ContentTypeInfo TypeInfo { get; }

    public string Title { get; }

    /// <summary>
    /// 中文名。<b>可能是空字符串</b> —— 说明这名字认不出来（纯品牌词），只显英文。
    /// </summary>
    /// <remarks>
    /// 由 <see cref="NameLocalizer"/> 的词表拼出来，不联网：<c>Iris Shaders</c> →
    /// 「Iris 光影」，<c>Sodium</c> → 空（社区叫它「钠」，但机翻给不了这个，
    /// 与其编一个，不如老实用英文）。
    /// </remarks>
    public string TitleZh { get; }

    /// <summary>标题行的大字：有中文名就用中文名，没有就用英文原名。</summary>
    public string TitleMain => HasTitleZh ? TitleZh : Title;

    /// <summary>标题行的小字：有中文名时是英文原名，否则为空（界面自己隐藏）。</summary>
    public string TitleSub => HasTitleZh ? Title : string.Empty;

    /// <summary>有没有中文名。PCL 那种「中文大字 + 英文小字」只在有中文时才成立。</summary>
    public bool HasTitleZh => TitleZh.Length > 0;

    public string Author { get; }

    public string Description { get; }

    public string Slug { get; }

    public string ProjectId { get; }

    /// <summary>「模组」/「整合包」/「光影包」/「资源包」。</summary>
    public string TypeLabel { get; }

    /// <summary>
    /// 这条能不能一键下载。
    /// </summary>
    /// <remarks>
    /// <b>整合包也走同一个按钮</b>，但点下去不是「丢进 mods」，而是问一个实例名、
    /// 新建实例、装原版和加载器、按 index 下几十个文件 —— 进度条会走好几个阶段。
    /// 两条路都在版本页里，用户不用记「这个类型该去哪儿下」。
    /// </remarks>
    public bool CanDownload => true;

    /// <summary>类型徽章的颜色。</summary>
    public string TypeColor { get; }

    /// <summary>加载器的显示名（Fabric、NeoForge…）。</summary>
    public IReadOnlyList<string> Loaders { get; }

    /// <summary>加载器拼成一行：<c>Fabric · NeoForge · Quilt</c>。</summary>
    public string LoaderText { get; }

    /// <summary>项目压根没标加载器（比如只写了功能分类）。</summary>
    public bool HasLoaderInfo { get; }

    public string DownloadsLabel { get; }

    public string FollowsLabel { get; }

    public string UpdatedLabel { get; }

    /// <summary>没有图标时的色块底色（Modrinth 给的主题色）。</summary>
    public string AccentColor { get; }

    /// <summary>色块上那个字。</summary>
    public string Initial { get; }

    public CompatState Compat { get; }

    /// <summary>版本徽章文字：<c>✓ 1.21.1</c> / <c>最高 1.20.4</c> / <c>最低 1.21.5</c>。</summary>
    public string VersionBadge { get; }

    /// <summary>徽章的完整解释，挂 ToolTip。</summary>
    public string VersionTip { get; }

    public bool IsCompatible => Compat == CompatState.Compatible;

    /// <summary>项目图标网址（<see cref="ContentIconLoader"/> 拿它下载）。</summary>
    public string? IconUrl => Hit.IconUrl;

    /// <summary>给状态栏用的一行摘要。</summary>
    public string Summary => $"{Title}（{TypeLabel}｜{LoaderText}｜{VersionBadge}）";

    // ── 计算 ─────────────────────────────────────────────

    /// <summary>从 categories 里挑出属于「这一类型」的加载器名。</summary>
    /// <remarks>
    /// 不能直接把 categories 全当加载器：Sodium 的 categories 是
    /// <c>[fabric, neoforge, optimization, quilt]</c>，其中 optimization 是功能分类。
    /// 所以拿当前类型的加载器白名单取交集，再映射成显示名。
    /// </remarks>
    private static IReadOnlyList<string> PickLoaders(ModrinthSearchHit hit, ContentTypeInfo typeInfo)
    {
        var allowed = typeInfo.Loaders;
        var result = new List<string>();

        foreach (var category in hit.Categories)
        {
            var match = allowed.FirstOrDefault(a => string.Equals(a, category, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;

            var label = ContentTypeInfo.LoaderLabel(match);

            // 资源包的「原版」不算加载器，显示出来只是噪音
            if (typeInfo.Type == ContentType.ResourcePack) continue;

            if (!result.Contains(label)) result.Add(label);
        }

        return result;
    }

    /// <summary>
    /// 判断和当前所选游戏版本的关系。
    /// </summary>
    /// <param name="supported">项目支持的全部游戏版本。</param>
    /// <param name="current">当前选中的版本（空 = 没选，不判断）。</param>
    /// <param name="rank">版本名 → 时间序号（越大越新）。</param>
    private static (CompatState State, string Badge, string Tip) DescribeVersion(
        IReadOnlyList<string> supported,
        string? current,
        IReadOnlyDictionary<string, int> rank)
    {
        if (supported.Count == 0)
            return (CompatState.Unknown, "版本未声明", "这个项目没有声明支持的游戏版本。");

        if (string.IsNullOrWhiteSpace(current))
        {
            var newest = supported[^1];
            return (CompatState.Unknown, $"支持 {supported.Count} 个版本",
                $"包含 {newest} 等 {supported.Count} 个版本。选一个游戏版本再搜，可以按版本筛。");
        }

        if (supported.Contains(current, StringComparer.OrdinalIgnoreCase))
            return (CompatState.Compatible, $"✓ {current}", $"支持当前选中的 {current}。");

        // 当前版本不在支持列表里 —— 找出「离它最近的」那个版本，告诉用户差多远
        if (!rank.TryGetValue(current, out var currentRank))
        {
            // 版本表里没有它（自定义版本名之类），只能给个模糊结论
            return (CompatState.Incompatible, "版本不匹配",
                $"不支持 {current}。该项目支持：{DescribeRange(supported, rank)}");
        }

        var older = supported
            .Select(v => (Version: v, Rank: rank.TryGetValue(v, out var r) ? r : -1))
            .Where(x => x.Rank >= 0 && x.Rank < currentRank)
            .OrderByDescending(x => x.Rank)
            .FirstOrDefault();

        var newer = supported
            .Select(v => (Version: v, Rank: rank.TryGetValue(v, out var r) ? r : -1))
            .Where(x => x.Rank > currentRank)
            .OrderBy(x => x.Rank)
            .FirstOrDefault();

        if (older.Version is not null)
        {
            return (CompatState.Incompatible, $"最高 {older.Version}",
                $"不支持 {current}，往下最接近的是 {older.Version}。完整支持范围：{DescribeRange(supported, rank)}");
        }

        if (newer.Version is not null)
        {
            return (CompatState.Incompatible, $"最低 {newer.Version}",
                $"不支持 {current}，它要求 {newer.Version} 或更新。完整支持范围：{DescribeRange(supported, rank)}");
        }

        return (CompatState.Incompatible, "版本不匹配",
            $"不支持 {current}。该项目支持：{DescribeRange(supported, rank)}");
    }

    /// <summary>「1.16.5 ~ 1.21.1」这种范围描述（按时间序取首尾）。</summary>
    private static string DescribeRange(IReadOnlyList<string> supported, IReadOnlyDictionary<string, int> rank)
    {
        var ordered = supported
            .OrderBy(v => rank.TryGetValue(v, out var r) ? r : int.MinValue)
            .ToList();

        if (ordered.Count == 0) return "—";
        if (ordered.Count == 1) return ordered[0];

        return $"{ordered[0]} ~ {ordered[^1]}（{ordered.Count} 个）";
    }

    /// <summary>2.56 亿 / 1081 万 / 3421。</summary>
    private static string FormatCount(long value) => value switch
    {
        >= 100_000_000 => $"{value / 100_000_000d:F1} 亿",
        >= 10_000 => $"{value / 10_000d:F1} 万",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    private static string FirstChar(string text)
    {
        if (string.IsNullOrEmpty(text)) return "?";
        var c = text[0];
        return char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c).ToString() : "?";
    }

    /// <summary>整数 RGB → <c>#RRGGBB</c>。太暗的返回 null，让调用方用类型色兜底。</summary>
    /// <remarks>
    /// 有的项目主题色接近纯黑（Iris Shaders 是 <c>#040404</c>），深色主题下色块会跟背景糊成一片。
    /// 亮度不够就不要它。
    /// </remarks>
    private static string? ToHex(int? rgb)
    {
        if (rgb is not { } value || value <= 0) return null;

        var clamped = value & 0xFFFFFF;
        var r = (clamped >> 16) & 0xFF;
        var g = (clamped >> 8) & 0xFF;
        var b = clamped & 0xFF;

        var luma = (0.299 * r) + (0.587 * g) + (0.114 * b);
        if (luma < 70) return null;

        return $"#{clamped:X6}";
    }
}
