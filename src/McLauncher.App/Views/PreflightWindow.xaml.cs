using System.Windows;
using McLauncher.Core.Mods;
using Wpf.Ui.Controls;

namespace McLauncher.App.Views;

/// <summary>
/// 启动前的模组体检结果。
/// </summary>
/// <remarks>
/// <para>
/// <b>只在对</b>——有 Error 时默认按钮是「取消启动」，有 Warning 时是「仍然启动」。
/// 用户点一次就能过，不该被一个提醒类的问题拽住。
/// </para>
/// <para>
/// <see cref="ShowDialog"/> 返回 <c>true</c> = 用户选择继续启动。
/// </para>
/// </remarks>
public partial class PreflightWindow : FluentWindow
{
    public PreflightWindow(ModPreflightReport report)
    {
        InitializeComponent();

        DataContext = report;

        FitToWorkArea();

        var errors = report.ErrorCount;
        var warnings = report.WarningCount;

        HeadlineText.Text = errors > 0
            ? "这套模组启动会出问题"
            : "有几个地方值得看一眼";

        // 有硬伤就把「但仍然启动」往下压：默认动作设成取消，回车不会误触
        FootHint.Text = errors > 0
            ? "这些问题不解决，游戏多半直接崩溃或拒绝加载模组。"
            : "只是提醒。能正常进游戏就说明没事，不用管。";

        ContinueButton.Content = errors > 0 ? "仍然启动（可能崩）" : "继续启动";

        if (errors > 0)
        {
            CancelButton.Appearance = ControlAppearance.Primary;
            ContinueButton.Appearance = ControlAppearance.Secondary;
        }

        _ = warnings;
    }

    /// <summary>200% 缩放下工作区只有约 1280×720 DIP，窗口得跟着缩。</summary>
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

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
