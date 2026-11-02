using System.Windows;
using DeepDive.App.ViewModels;
using Wpf.Ui.Controls;

namespace DeepDive.App.Views;

/// <summary>
/// 文件完整性校验窗口：深度校验一个版本的所有文件，列出问题，一键修复。
/// </summary>
public partial class IntegrityWindow : FluentWindow
{
    private readonly IntegrityViewModel _viewModel;

    public IntegrityWindow(IntegrityViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        FitToWorkArea();

        // 窗口出来就自动跑一次深度校验，省得用户再点「开始校验」
        Loaded += async (_, _) => await viewModel.CheckCommand.ExecuteAsync(null);
        Closed += (_, _) => viewModel.Close();
    }

    /// <summary>和主窗口同一套：200% 缩放下工作区只有约 1280×720 DIP。</summary>
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
}
