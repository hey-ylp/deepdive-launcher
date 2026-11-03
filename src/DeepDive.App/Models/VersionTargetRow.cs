using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DeepDive.Core.Modrinth;

namespace DeepDive.App.Models;

/// <summary>
/// 版本选择列表里的一行 = 一个「加载器 + 游戏版本」组合（<c>NeoForge 1.21.1</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么按组合而不是按文件列表</b>：一个热门项目（Sodium）有 258 个文件版本 ——
/// 平铺出来是一屏接一屏的 <c>mc1.21.1-0.5.11</c>、<c>mc1.21-0.5.10</c>…
/// 用户真正要回答的问题只有一个：「我玩 1.21.1 + NeoForge，给我装哪个？」
/// </para>
/// <para>
/// 所以先按游戏版本筛（窗口顶部那排标签），再按加载器分组成一行，
/// 每行直接指向这个组合下最新的那个文件。想装旧版再点「其他版本」。
/// 这也是 PCL 下载页的样子。
/// </para>
/// </remarks>
public sealed partial class VersionTargetRow : ObservableObject
{
    /// <param name="loader">加载器原名（<c>fabric</c> / <c>neoforge</c> …），空 = 没标加载器。</param>
    /// <param name="gameVersion">当前选中的游戏版本。</param>
    /// <param name="versions">这个组合下的全部文件版本，<b>已按发布时间从新到旧排好</b>。</param>
    /// <param name="matchesGameVersion">游戏版本和当前实例一致。</param>
    /// <param name="matchesLoader">加载器和当前实例一致（不需要比加载器的类型直接传 true）。</param>
    public VersionTargetRow(
        string loader,
        string gameVersion,
        IReadOnlyList<ModrinthVersion> versions,
        bool matchesGameVersion,
        bool matchesLoader)
    {
        Loader = loader;
        GameVersion = gameVersion;
        Versions = versions;
        MatchesGameVersion = matchesGameVersion;
        MatchesLoader = matchesLoader;

        Latest = versions[0];
        LatestNumber = string.IsNullOrWhiteSpace(Latest.VersionNumber) ? Latest.Name : Latest.VersionNumber;
        VersionCount = versions.Count;

        (TypeLabel, TypeColor) = Latest.VersionType switch
        {
            "release" => ("正式版", "#0F7B0F"),
            "beta" => ("测试版", "#C05621"),
            "alpha" => ("预览版", "#9C36B5"),
            _ => (Latest.VersionType, "#6B7280"),
        };

        var size = PrimaryFile?.Size ?? 0;
        SizeText = FormatSize(size);
        DateText = Latest.DatePublished == default
            ? "—"
            : Latest.DatePublished.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        RequiredDependencies = Latest.Dependencies
            .Count(d => string.Equals(d.DependencyType, "required", StringComparison.OrdinalIgnoreCase));

        // 先摆 ID，名字等视图模型批量查到再改。没有前置就留空，ToolTip 也不挂。
        DependencyDetail = RequiredDependencies == 0
            ? string.Empty
            : "前置：" + string.Join("、", RequiredDependencyIds);
    }

    /// <summary>加载器原名。</summary>
    public string Loader { get; }

    /// <summary>「NeoForge」/「Fabric」/「原版」。</summary>
    public string LoaderLabel => string.IsNullOrWhiteSpace(Loader)
        ? "通用"
        : ContentTypeInfo.LoaderLabel(Loader);

    public string GameVersion { get; }

    /// <summary>行标题：<c>NeoForge 1.21.1</c>。</summary>
    public string Title => $"{LoaderLabel} {GameVersion}";

    /// <summary>这个组合下最新的那个版本（下载就下它）。</summary>
    public ModrinthVersion Latest { get; }

    /// <summary>组合下的全部版本，新 → 旧。</summary>
    public IReadOnlyList<ModrinthVersion> Versions { get; }

    public string LatestNumber { get; }

    public int VersionCount { get; }

    public string TypeLabel { get; }

    public string TypeColor { get; }

    public string SizeText { get; }

    public string DateText { get; }

    /// <summary>游戏版本和当前实例一致。</summary>
    public bool MatchesGameVersion { get; }

    /// <summary>加载器和当前实例一致。</summary>
    public bool MatchesLoader { get; }

    /// <summary>
    /// 能不能直接装：版本和加载器都得对。
    /// </summary>
    /// <remarks>
    /// 两个都要判。<c>Fabric 1.20.1</c> 的 jar 丢进 1.21.1 的 NeoForge 实例，
    /// 轻则加载器不认、模组列表里根本不出现，重则启动直接崩 —— 而且崩溃日志只会说
    /// 「某个模组加载失败」，用户根本想不到是自己下错了版本。
    /// </remarks>
    public bool IsInstanceMatch => MatchesGameVersion && MatchesLoader;

    /// <summary>跟当前实例对不上 —— 界面标橙、点下载要拦一道。</summary>
    public bool IsMismatch => !IsInstanceMatch;

    /// <summary>
    /// 对不上的具体原因，给确认框用。
    /// </summary>
    /// <param name="instanceGameVersion">当前实例的游戏版本。</param>
    /// <param name="instanceLoaderLabel">当前实例的加载器显示名。</param>
    public string DescribeMismatch(string? instanceGameVersion, string? instanceLoaderLabel)
    {
        var problems = new List<string>();

        if (!MatchesGameVersion)
        {
            problems.Add(instanceGameVersion is null
                ? $"你选的是 {GameVersion}，当前实例的游戏版本没识别出来"
                : $"这个文件是给 {GameVersion} 的，当前实例是 {instanceGameVersion}");
        }

        if (!MatchesLoader)
        {
            problems.Add(instanceLoaderLabel is null
                ? $"这个文件是 {LoaderLabel} 版的，当前实例的加载器没识别出来"
                : $"这个文件是 {LoaderLabel} 版的，当前实例用的是 {instanceLoaderLabel}");
        }

        return problems.Count == 0
            ? string.Empty
            : string.Join("；", problems) + "。";
    }

    public int RequiredDependencies { get; }

    public bool HasDependencies => RequiredDependencies > 0;

    public string DependencyText => RequiredDependencies switch
    {
        0 => string.Empty,
        1 => "带 1 个前置",
        _ => $"带 {RequiredDependencies} 个前置",
    };

    /// <summary>
    /// 悬停时显示的前置清单。**先给 ID，名字是异步补的**。
    /// </summary>
    /// <remarks>
    /// 版本窗口一屏十几行，同步查名字会把界面卡住；所以先把 <c>project_id</c> 摆上去，
    /// 批量查到名字再由视图模型改写这个属性。查不到就一直显示 ID —— 有总比没有强。
    /// </remarks>
    [ObservableProperty]
    private string _dependencyDetail = string.Empty;

    /// <summary>必需前置的项目 ID（去重，供视图模型批量查名字）。</summary>
    public IReadOnlyList<string> RequiredDependencyIds => Latest.Dependencies
        .Where(d => string.Equals(d.DependencyType, "required", StringComparison.OrdinalIgnoreCase))
        .Select(d => d.ProjectId)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Select(id => id!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>要下的文件：优先 <c>primary</c>。</summary>
    public ModrinthVersionFile? PrimaryFile =>
        Latest.Files.FirstOrDefault(f => f.Primary) ?? Latest.Files.FirstOrDefault();

    public bool HasFile => PrimaryFile is not null;

    /// <summary>「最新 0.8.13 · 正式版 · 共 5 个版本」。</summary>
    public string MetaText =>
        $"最新 {LatestNumber} · {DateText} · {SizeText} · 共 {VersionCount} 个版本";

    /// <summary>这一行自己的状态（空 = 还没动过）。</summary>
    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _isDone;

    /// <summary>下完了。</summary>
    public void MarkDone(string text)
    {
        IsDownloading = false;
        IsDone = true;
        StateText = text;
    }

    /// <summary>失败了。</summary>
    public void MarkFailed(string text)
    {
        IsDownloading = false;
        IsDone = false;
        StateText = text;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:F0} KB",
        _ => $"{bytes / 1024d / 1024d:F1} MB",
    };
}
