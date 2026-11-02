using System.Windows;
using McLauncher.App.Models;
using McLauncher.App.ViewModels;
using McLauncher.Core.Modrinth;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace McLauncher.App.Views;

/// <summary>
/// 内容下载窗口：按类型 / 游戏版本 / 加载器搜 Modrinth。
/// </summary>
/// <remarks>
/// <b>已废弃，别再往这里改</b>：2026-10-31 起内容浏览是左导航的「内容」页
/// （<c>Views/Pages/ContentPage.xaml</c>），界面和这个窗口是同一份版式。
/// 这里只留作对照参考，任何功能改动都要改页面那一份，否则两边会走样。
/// </remarks>
public partial class ContentBrowserWindow : FluentWindow
{
    private readonly ContentBrowserViewModel _viewModel;

    public ContentBrowserWindow(ContentBrowserViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        FitToWorkArea();

        // 版本表要在窗口出来之后才拉 —— 拉表期间界面得先能看见，不然像卡死
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        Closed += (_, _) => viewModel.CancelPendingSearch();
    }

    /// <summary>
    /// 点「下载」：开版本选择窗口。
    /// </summary>
    /// <remarks>
    /// 放代码里而不是命令里，是因为它要 new 一个窗口 —— 视图模型不该知道窗口。
    /// 依赖从容器现取：这个窗口开关很多次，没必要让视图模型攥着下载器不放。
    /// </remarks>
    private void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ContentHitView hit }) return;

        var viewModel = new ContentVersionViewModel(
            App.Services.GetRequiredService<ModrinthClient>(),
            App.Services.GetRequiredService<ContentInstaller>(),
            App.Services.GetRequiredService<Core.Launcher.GameFolderService>(),
            hit,
            _viewModel.VersionName,
            _viewModel.BaseMcVersion,
            App.Services.GetRequiredService<ModpackInstaller>(),
            _viewModel.OnInstanceCreated);

        new ContentVersionWindow(viewModel) { Owner = this }.Show();
    }

    /// <summary>
    /// 和主窗口同一套：1920×1080 + 200% 缩放下工作区只有约 1280×720 DIP，
    /// 写死 700 高会顶出屏幕。
    /// </summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        const int margin = 24;

        if (Width > area.Width - margin)
            Width = Math.Max(MinWidth, area.Width - margin);

        if (Height > area.Height - margin)
            Height = Math.Max(MinHeight, area.Height - margin);

        if (MinHeight > area.Height - margin)
            MinHeight = Math.Max(420, area.Height - margin);
    }
}
