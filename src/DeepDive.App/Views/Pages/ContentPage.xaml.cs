using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DeepDive.App.Models;
using DeepDive.App.ViewModels;
using DeepDive.App.Views;
using DeepDive.Core.Launcher;
using DeepDive.Core.Modrinth;
using DeepDive.Core.Translation;
using Microsoft.Extensions.DependencyInjection;

namespace DeepDive.App.Views.Pages;

/// <summary>
/// 内容页：按类型 / 游戏版本 / 加载器搜 Modrinth，直接装进当前实例。
/// </summary>
/// <remarks>
/// <para>
/// <b>视图模型按需建，不是构造时就建</b>：页面是常驻的（只切 Visibility），
/// 而搜索目标取决于「当前实例 + 当前版本」。启动时还不知道用户会选哪个版本，
/// 提前建只会白拉一次版本表、白发一次搜索，还多一份没人看的状态。
/// 现在改成「第一次真的显示出来」才建，切版本时再重建。
/// </para>
/// <para>
/// <b>重建前必须 <see cref="ContentBrowserViewModel.CancelPendingSearch"/></b>：
/// 翻译服务和 Modrinth 客户端都是单例，视图模型订阅了它们的事件却没有别的退订时机，
/// 漏一次就永久泄漏一份搜索结果。
/// </para>
/// </remarks>
public partial class ContentPage : UserControl
{
    private ContentBrowserViewModel? _browser;

    /// <summary>当前这份视图模型绑的目标（实例 | 版本 | 原版号）。变了就重建。</summary>
    private string _targetKey = string.Empty;

    /// <summary>防重入：切页面很快时会连发几次可见性变化。</summary>
    private bool _switching;

    public ContentPage()
    {
        InitializeComponent();

        IsVisibleChanged += OnVisibleChanged;
    }

    private MainViewModel? Owner => DataContext as MainViewModel;

    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // 只在「变成可见」这一刻动手；切走不用管，在飞的请求留着也无害
        if (e.NewValue is not true) return;

        try
        {
            await EnsureBrowserAsync();
        }
        catch (Exception ex)
        {
            // 内容页崩了不该拖垮整个启动器（历史教训：异步异常会直接让进程静默退出）
            Owner?.AppendLog($"[内容] 页面初始化异常：{ex.GetType().Name}: {ex.Message}");
            TargetHint.Text = "内容页加载失败，看日志";
        }
    }

    /// <summary>目标变了就重建视图模型并拉版本表，没变就什么都不做。</summary>
    private async Task EnsureBrowserAsync()
    {
        if (_switching) return;
        if (Owner is not { } main) return;

        if (main.SelectedVersion is not { } version)
        {
            TargetHint.Text = "还没选版本 —— 先去「版本」页挑一个";
            return;
        }

        var key = $"{main.SelectedInstance?.Name}|{version.Name}|{main.SelectedBaseMcVersion}";
        if (key == _targetKey && _browser is not null) return;

        _switching = true;
        try
        {
            // 掐掉上一份在飞的搜索并退订单例事件，否则每切一次版本泄漏一个视图模型
            _browser?.CancelPendingSearch();
            _browser = null;
            _targetKey = key;

            TargetHint.Text = $"当前：{main.SelectedInstance?.Name ?? "?"} · {version.Name}";

            var browser = new ContentBrowserViewModel(
                App.Services.GetRequiredService<ModrinthClient>(),
                App.Services.GetRequiredService<GameFolderService>(),
                App.Services.GetRequiredService<TranslationService>(),
                version.Name,
                main.SelectedBaseMcVersion,
                main.AppendLog,
                // 装完整合包会新建实例 —— 主界面的实例列表得知道
                instanceName => main.ReloadInstances(instanceName));

            BrowserRoot.DataContext = browser;
            _browser = browser;

            await browser.InitializeAsync();
        }
        finally
        {
            _switching = false;
        }
    }

    /// <summary>
    /// 点「下载」：开版本选择窗口。
    /// </summary>
    /// <remarks>
    /// 放代码里而不是命令里，是因为它要 new 一个窗口 —— 视图模型不该知道窗口。
    /// 依赖从容器现取：这个操作会反复触发，没必要让视图模型攥着下载器不放。
    /// </remarks>
    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { DataContext: ContentHitView hit }) return;
            if (_browser is not { } browser) return;

            var viewModel = new ContentVersionViewModel(
                App.Services.GetRequiredService<ModrinthClient>(),
                App.Services.GetRequiredService<ContentInstaller>(),
                App.Services.GetRequiredService<GameFolderService>(),
                hit,
                browser.VersionName,
                browser.BaseMcVersion,
                App.Services.GetRequiredService<ModpackInstaller>(),
                browser.OnInstanceCreated);

            new ContentVersionWindow(viewModel) { Owner = Window.GetWindow(this) }.Show();
        }
        catch (Exception ex)
        {
            Owner?.AppendLog($"[内容] 打开版本选择失败：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
