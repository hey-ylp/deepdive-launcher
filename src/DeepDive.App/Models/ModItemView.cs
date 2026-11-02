using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DeepDive.Core.Mods;
using DeepDive.Core.Translation;

namespace DeepDive.App.Models;

/// <summary>
/// 模组管理列表里的一行（UI 版）。
/// </summary>
/// <remarks>
/// 和 <see cref="ManagedMod"/> 分开是因为那个在 Core 里，不能碰 WPF 的 <see cref="ImageSource"/>；
/// 而且启用/禁用之后要整体刷新一遍显示，需要一个能发通知的壳。
/// </remarks>
public partial class ModItemView : ObservableObject
{
    /// <summary>底层数据。启用/禁用/删除后会被换掉。</summary>
    public ManagedMod Model { get; private set; }

    public ModItemView(ManagedMod model) => Model = model;

    /// <summary>图标。取不到（jar 里没图、或图坏了）就是 <c>null</c>，界面显示占位方块。</summary>
    [ObservableProperty]
    private ImageSource? _icon;

    /// <summary>
    /// 从 MC百科查到的中文名。查不到就是空 —— 大部分模组本来就只有一个英文名。
    /// </summary>
    /// <remarks>
    /// 查是异步的（要联网、还要限速），所以列表先按原名显示，这个名字是后补进来的。
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChineseName))]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(ShowOriginalName))]
    private string _chineseName = string.Empty;

    /// <summary>正在处理（启用/禁用请求在飞）。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>命中当前搜索/筛选条件。由视图模型统一切，界面用 Visibility 绑。</summary>
    [ObservableProperty]
    private bool _isVisible = true;

    // ── 转发给界面的只读属性 ──

    /// <summary>有没有中文名。没有就只显示原名，别留空行。</summary>
    public bool HasChineseName => !string.IsNullOrEmpty(ChineseName);

    /// <summary>
    /// 本地词表拼出来的名字（离线也能有），懒算后缓存。
    /// </summary>
    /// <remarks>
    /// <see cref="NameLocalizer"/> 里有一张社区惯用译名表（sodium → 钠、create → 机械动力），
    /// 外加通用词拼接（Shaders → 光影）。全在内存里，不联网、不用等，
    /// 所以列表一出来就能用 —— 联网查到的名字是「更好的结果」，这个是「立刻有的结果」。
    /// </remarks>
    public string LocalName
    {
        get
        {
            if (_localName is null)
            {
                _localName = NameLocalizer.ChineseTitle(
                    Model.DisplayName,
                    Model.Metadata?.ModId) ?? string.Empty;
            }

            return _localName;
        }
    }

    private string? _localName;

    /// <summary>
    /// 主标题：联网查到的中文名 &gt; 本地词表拼的 &gt; jar 里写的原名。
    /// </summary>
    public string DisplayName =>
        HasChineseName ? ChineseName
        : LocalName.Length > 0 ? LocalName
        : Model.DisplayName;

    /// <summary>jar 里写的原始名字。主标题不是它的时候退到后面当小字。</summary>
    public string OriginalName => Model.DisplayName;

    /// <summary>要不要显示那行小字原名。</summary>
    public bool ShowOriginalName =>
        !string.Equals(DisplayName, Model.DisplayName, StringComparison.Ordinal);

    public string FileName => Model.FileName;
    public string VersionLabel => Model.VersionLabel;
    public string AuthorsLabel => Model.AuthorsLabel;
    public string ModIdLabel => Model.ModIdLabel;
    public string DescriptionLabel => Model.DescriptionLabel;
    public string LoaderLabel => Model.LoaderLabel;
    public string StateLabel => Model.StateLabel;
    public string SizeLabel => Model.SizeLabel;

    public bool IsEnabled => Model.IsEnabled;

    /// <summary>解析失败（不是模组 / jar 坏了）。界面标红、禁用开关。</summary>
    public bool IsBroken => Model.Metadata is null;

    /// <summary>有作者或简介 —— 用来决定第二行要不要显示。</summary>
    public bool HasDetail =>
        !string.IsNullOrWhiteSpace(Model.AuthorsLabel) || !string.IsNullOrWhiteSpace(Model.DescriptionLabel);

    /// <summary>第二行文字：「作者：xxx · 简介」。没作者就只放简介。</summary>
    public string DetailLine
    {
        get
        {
            var parts = new List<string>(2);

            if (!string.IsNullOrWhiteSpace(Model.AuthorsLabel))
                parts.Add($"作者：{Model.AuthorsLabel}");

            if (!string.IsNullOrWhiteSpace(Model.DescriptionLabel))
                parts.Add(Model.DescriptionLabel!);

            return string.Join("　·　", parts);
        }
    }

    /// <summary>换掉底层数据（启用/禁用后文件名变了）并刷新所有显示属性。</summary>
    public void Update(ManagedMod model)
    {
        Model = model;
        _localName = null;   // 换了文件（可能换了模组），本地名要重算

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(LocalName));
        OnPropertyChanged(nameof(OriginalName));
        OnPropertyChanged(nameof(ShowOriginalName));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(AuthorsLabel));
        OnPropertyChanged(nameof(ModIdLabel));
        OnPropertyChanged(nameof(DescriptionLabel));
        OnPropertyChanged(nameof(LoaderLabel));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsBroken));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailLine));
    }
}
