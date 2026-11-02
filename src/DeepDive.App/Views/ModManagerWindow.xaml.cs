using System.Diagnostics;
using System.IO;
using System.Windows;
using DeepDive.App.ViewModels;
using Wpf.Ui.Controls;

namespace DeepDive.App.Views;

/// <summary>
/// 模组管理窗口：列出目录里的所有 jar，能搜、能筛、能启用禁用、能移除。
/// </summary>
/// <remarks>
/// 交互上刻意不做「点一下开关就切」的即时切换：启用/禁用要改文件名，
/// 误点一次游戏就少一个模组，而用户完全不知道发生了什么。做成明确的按钮，
/// 点完还有一行日志说明动了哪个文件。
/// </remarks>
public partial class ModManagerWindow : FluentWindow
{
    private readonly ModManagerViewModel _viewModel;

    private readonly string _modsDirectory;

    public ModManagerWindow(ModManagerViewModel viewModel, string modsDirectory)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _modsDirectory = modsDirectory;

        DataContext = viewModel;

        FitToWorkArea();

        Loaded += async (_, _) => await viewModel.ReloadAsync();

        Closed += (_, _) => viewModel.CancelPending();
    }

    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        const int margin = 24;

        if (Width > area.Width - margin)
            Width = Math.Max(MinWidth, area.Width - margin);

        if (Height > area.Height - margin)
            Height = Math.Max(MinHeight, area.Height - margin);

        if (MinHeight > area.Height - margin)
            MinHeight = Math.Max(400, area.Height - margin);
    }

    /// <summary>
    /// 交给系统打开。资源管理器坏了也不影响这个窗口本身 —— 用户还能在里面继续操作。
    /// </summary>
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(_modsDirectory))
            {
                Directory.CreateDirectory(_modsDirectory);
            }

            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = _modsDirectory,
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            });
        }
        catch (Exception ex)
        {
            // 全限定：Wpf.Ui.Controls 里也有同名类型，using 了两个都进来会二义
            System.Windows.MessageBox.Show(
                $"系统打不开这个目录：{ex.Message}\n\n路径：{_modsDirectory}",
                "打开文件夹失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }
}
