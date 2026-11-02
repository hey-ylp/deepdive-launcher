using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DeepDive.App.Models;
using DeepDive.App.ViewModels;

namespace DeepDive.App.Views.Pages;

/// <summary>
/// 版本页：列版本、看详情、装加载器、卸载、校验。
/// </summary>
/// <remarks>
/// 搜索和「只看已装」都做在<b>默认视图</b>上（<see cref="CollectionViewSource.GetDefaultView"/>），
/// 不复制一份集合 —— 列表和启动页、切换菜单共用同一个 <c>Versions</c> 和同一个选中项，
/// 一旦复制，两边就会各选各的。
/// </remarks>
public partial class VersionsPage : UserControl
{
    private string _search = string.Empty;
    private bool _installedOnly;

    /// <summary>渠道筛选：<c>all</c> / <c>release</c> / <c>snapshot</c> / <c>old</c>。</summary>
    private string _channel = "all";

    /// <summary>正在把 ViewModel 的选中项同步到列表，这期间的 SelectionChanged 要忽略。</summary>
    private bool _syncingSelection;

    public VersionsPage()
    {
        InitializeComponent();

        // DataContext 是窗口建完之后才继承进来的，构造函数里还拿不到
        DataContextChanged += OnDataContextChanged;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        // 过滤器挂一次就够：Versions 被清空重填时默认视图自己会跟着变，不用重新挂钩
        var view = CollectionViewSource.GetDefaultView(viewModel.Versions);
        view.Filter = FilterVersion;

        SyncSelectionFromViewModel();
    }

    /// <summary>一个版本条目过不过滤：渠道 → 已装 → 搜索词，三层都过才显示。</summary>
    private bool FilterVersion(object item)
    {
        if (item is not VersionEntry entry) return false;

        if (!MatchChannel(entry.Channel)) return false;
        if (_installedOnly && !entry.IsInstalled) return false;

        return _search.Length == 0
               || entry.Name.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 渠道匹配。
    /// </summary>
    /// <remarks>
    /// 远古版在清单里是两个值（<c>old_alpha</c> / <c>old_beta</c>），界面上合成一个按钮，
    /// 所以这里要一起认。<c>local</c>（认不出渠道的本地版本）只在「全部」里出现 —— 宁可少筛，不可漏版本。
    /// </remarks>
    private bool MatchChannel(string channel) => _channel switch
    {
        "release" => channel == "release",
        "snapshot" => channel == "snapshot",
        "old" => channel is "old_beta" or "old_alpha",
        _ => true,
    };

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        RefreshList();
    }

    private void OnInstalledOnlyChanged(object sender, RoutedEventArgs e)
    {
        _installedOnly = InstalledOnlyCheck.IsChecked == true;
        RefreshList();
    }

    private void OnChannelChecked(object sender, RoutedEventArgs e)
    {
        // 这个事件在 InitializeComponent 解析「全部」的 IsChecked 时就会触发 ——
        // 那时 ViewModel 还是 null，RefreshList 自己会早退，这里只要别炸
        _channel = (sender as FrameworkElement)?.Tag as string ?? "all";
        RefreshList();
    }

    private void RefreshList()
    {
        if (ViewModel is not { } viewModel) return;

        try
        {
            CollectionViewSource.GetDefaultView(viewModel.Versions).Refresh();
            SyncSelectionFromViewModel();
        }
        catch (Exception ex)
        {
            // 过滤炸了不该让整个界面不可用 —— 记一行日志，列表退化成不过滤
            viewModel.AppendLog($"[界面] 版本列表过滤失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 列表选中 → 当前版本。
    /// </summary>
    /// <remarks>
    /// <b>不能直接双向绑定</b>：筛选把选中项藏起来时，ListBox 会把 <c>SelectedItem</c> 清成 null，
    /// 双向绑定就顺手把「当前版本」也清空了 —— 表现是切一下「正式版」，启动页的卡片突然变空。
    /// 这里只认「选到了具体某一项」，清空一律不理；恢复可见时由 <see cref="SyncSelectionFromViewModel"/> 补回来。
    /// </remarks>
    private void OnVersionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (ViewModel is not { } viewModel) return;
        if (VersionList.SelectedItem is not VersionEntry entry) return;
        if (ReferenceEquals(entry, viewModel.SelectedVersion)) return;

        viewModel.SelectedVersion = entry;
    }

    /// <summary>当前版本 → 列表高亮（过滤后那一项看不见就保持未选中，不动 ViewModel）。</summary>
    private void SyncSelectionFromViewModel()
    {
        if (ViewModel is not { } viewModel) return;

        _syncingSelection = true;
        try
        {
            var visible = false;
            foreach (var item in CollectionViewSource.GetDefaultView(viewModel.Versions))
                if (ReferenceEquals(item, viewModel.SelectedVersion)) { visible = true; break; }

            VersionList.SelectedItem = visible ? viewModel.SelectedVersion : null;
        }
        finally
        {
            _syncingSelection = false;
        }
    }
}
