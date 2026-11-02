using System.Diagnostics;
using System.IO;
using System.Windows;
using DeepDive.App.ViewModels;

namespace DeepDive.App.Views;

/// <summary>崩溃分析窗口：只负责显示结论和两个按钮。</summary>
public partial class CrashWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly CrashViewModel _viewModel;

    public CrashWindow(CrashViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = viewModel;
    }

    /// <summary>用资源管理器打开崩溃报告（选中文件，方便直接拖给别人）。</summary>
    private void OnOpenReportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _viewModel.ReportPath;

            if (!File.Exists(path))
            {
                MessageBox.Show("崩溃报告文件不在了（可能被清理软件删了）。", "打开失败",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // /select 而不是直接打开：崩溃报告动辄几百 KB，
            // 用户大多是想「发给别人看」，选中文件比开记事本更有用
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开失败：{ex.Message}", "打开崩溃报告",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
