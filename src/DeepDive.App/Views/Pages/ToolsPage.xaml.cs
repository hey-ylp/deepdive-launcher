using System;
using System.Windows;
using System.Windows.Controls;
using DeepDive.App.ViewModels;

namespace DeepDive.App.Views.Pages;

public partial class ToolsPage : UserControl
{
    public ToolsPage()
    {
        InitializeComponent();

        // 每次切到这一页就重扫存档：刚在游戏里新建的世界，切回来就能看见，
        // 不用重启启动器。扫一个目录很快，不值得为它做缓存。
        IsVisibleChanged += OnVisibleChanged;
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;

        if (DataContext is not MainViewModel viewModel) return;

        // 页面里出问题不该连累别的功能，记一行日志就够
        try
        {
            viewModel.ReloadSavesCommand.Execute(null);
        }
        catch (Exception ex)
        {
            viewModel.AppendLog($"[界面] 刷新存档列表失败：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
