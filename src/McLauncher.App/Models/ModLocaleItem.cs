using CommunityToolkit.Mvvm.ComponentModel;
using McLauncher.Core.Translation;

namespace McLauncher.App.Models;

/// <summary>
/// 工具页「模组汉化」列表里的一行。
/// </summary>
/// <remarks>
/// 包一层的原因：<see cref="ModLocaleInfo"/> 是 readonly record，翻完之后状态要变
/// （「纯英文，312 条待翻」→「已汉化，312 条」），record 改不了，得有可变外壳。
/// </remarks>
public sealed partial class ModLocaleItem : ObservableObject
{
    public ModLocaleItem(ModLocaleInfo info)
    {
        Info = info;
        _statusText = info.StatusText;
        _needsWork = info.NeedsWork;
        _localizedByPack = info.LocalizedByPack;
    }

    /// <summary>原始扫描结果。</summary>
    public ModLocaleInfo Info { get; }

    /// <summary>显示名（mods.toml 里的 displayName，读不到就是文件名前缀）。</summary>
    public string DisplayName => Info.DisplayName;

    /// <summary>jar 文件名，鼠标悬停时给用户看清楚是哪一个。</summary>
    public string FileName => Info.FileName;

    /// <summary>状态文字，翻完会更新。</summary>
    [ObservableProperty]
    private string _statusText;

    /// <summary>还有没有要翻的（决定「汉化」按钮能不能点）。</summary>
    [ObservableProperty]
    private bool _needsWork;

    /// <summary>已经由资源包补全中文了（下次扫描也是这个状态，不会又变回「待翻」）。</summary>
    [ObservableProperty]
    private bool _localizedByPack;

    /// <summary>这一行正在处理。</summary>
    [ObservableProperty]
    private bool _isBusy;
}
