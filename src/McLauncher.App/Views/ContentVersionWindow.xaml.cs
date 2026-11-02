using System.Windows;
using McLauncher.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace McLauncher.App.Views;

/// <summary>
/// 下载窗口：列出一个项目的所有版本，挑一个直接下到实例目录。
/// </summary>
public partial class ContentVersionWindow : FluentWindow
{
    private readonly ContentVersionViewModel _viewModel;

    public ContentVersionWindow(ContentVersionViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // 整合包要新建实例：问她叫名字，顺便知道哪些名字已经被占了
        var instances = App.Services.GetRequiredService<Core.Launcher.InstanceManager>();

        viewModel.AskInstanceName = suggested => TextInputWindow.Ask(
            this,
            "装成新实例",
            "整合包自带游戏版本和加载器，会单独建一个实例，不会动你现在这个。\n给它起个名字：",
            suggested,
            "重名会自动加序号");

        viewModel.InstanceExists = name => instances
            .Scan()
            .Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

        FitToWorkArea();

        // 版本列表要几 MB，窗口先出来再拉 —— 不然点下载之后界面会“卡”一两秒才出现
        Loaded += async (_, _) => await viewModel.InitializeAsync();
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
