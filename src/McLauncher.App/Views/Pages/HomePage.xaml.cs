using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using McLauncher.App.ViewModels;
using McLauncher.App.Views;
using McLauncher.Core.Launcher;
using McLauncher.Core.Modrinth;
using McLauncher.Core.Translation;
using Microsoft.Extensions.DependencyInjection;

namespace McLauncher.App.Views.Pages;

/// <summary>
/// 启动页：选实例、选版本、点启动、看日志。
/// </summary>
/// <remarks>
/// 这一页故意不放任何设置项（下载源、内存、账号都在设置页）。
/// 进来的第一眼只需要回答一件事：<b>要不要启动</b>。
/// </remarks>
public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>
    /// 打开内容下载窗口。搜索条件按当前选中的版本预填。
    /// </summary>
    /// <remarks>
    /// 为什么非要先选版本：模组和光影的搜索条件里，游戏版本和加载器是硬条件。
    /// 拿不到当前版本就没法预填，用户进去面对一堆「不限」，搜出来全是别的版本能用的东西，
    /// 装上去直接崩 —— 不如先拦住。
    /// </remarks>
    /// <summary>
    /// 跳到「内容」页。
    /// </summary>
    /// <remarks>
    /// 为什么非要先选版本：模组和光影的搜索条件里，游戏版本和加载器是硬条件。
    /// 拿不到当前版本就没法预填，用户进去面对一堆「不限」，搜出来全是别的版本能用的东西，
    /// 装上去直接崩 —— 不如先拦住。
    /// <para>
    /// 以前这里是开一个独立窗口（<c>ContentBrowserWindow</c>）。现在内容浏览是左导航的一页，
    /// 这里只负责跳过去 —— 搜索界面不该有两份，否则改一处忘一处。
    /// </para>
    /// </remarks>
    private void OnOpenContentBrowserClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        if (viewModel.SelectedVersion is null)
        {
            // 明确写 System.Windows.MessageBox：Wpf.Ui.Controls 里也有一个同名类型
            MessageBox.Show(
                "先在「游戏版本」里选一个版本。\n\n搜索会拿它的游戏版本（比如 1.21.1）和加载器（比如 NeoForge）去筛，"
                + "结果里每条都会标出兼不兼容你选的版本。",
                "先选一个版本",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        (Window.GetWindow(this) as MainWindow)?.ShowPage("content");
    }

    /// <summary>
    /// 「切换版本」按钮：弹一个菜单，上面是实例，下面是游戏版本。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么不用下拉框：卡片式的版式里塞两个 ComboBox 会把主按钮挤下去（PCL 也不这么放）。
    /// 但实例和版本又必须能切 —— 所以合成一个菜单，顺手把「新建 / 重命名 / 删除实例」也塞进去，
    /// 免得那三个操作没有落脚点。
    /// </para>
    /// <para>
    /// 菜单在<b>每次点击时现建</b>：实例和版本列表是异步刷新的，缓存一份菜单会拿到过期数据。
    /// </para>
    /// </remarks>
    private void OnSwitchVersionClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || sender is not Button button) return;

        try
        {
            // 挂到按钮自己的 ContextMenu 上：WPF 会自动处理定位、阴影、点外面关闭，
            // 手动 PlacementTarget + IsOpen 反而要自己收尾，还拿不到这些默认行为
            button.ContextMenu = BuildSwitchMenu(viewModel);
            button.ContextMenu.IsOpen = true;
        }
        catch (Exception ex)
        {
            viewModel.AppendLog($"[界面] 版本菜单打不开：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 拼「切换版本」菜单：实例 → 已装版本 → 管理项。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>菜单里绝不放远端版本。</b>Mojang 清单有九百多条，全塞进来会出两件事：
    /// 一是 ContextMenu 的模板没有滚动条，满了就<b>划不动</b>；二是「新建实例」被顶到九百项后面，
    /// 用户根本够不着 —— 实测就是这么翻车的。
    /// </para>
    /// <para>
    /// 要挑没装的版本，去「版本」页（有搜索和渠道筛选），菜单里只留一个入口。
    /// </para>
    /// </remarks>
    private ContextMenu BuildSwitchMenu(MainViewModel viewModel)
    {
        var menu = new ContextMenu { MinWidth = 260 };

        menu.Items.Add(Section("实例"));

        foreach (var instance in viewModel.Instances)
        {
            var target = instance;
            menu.Items.Add(new MenuItem
            {
                Header = instance.Display,
                IsCheckable = true,
                IsChecked = ReferenceEquals(instance, viewModel.SelectedInstance),
                Command = new RelayCommandShim(() => viewModel.SelectedInstance = target),
            });
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Section("已装版本"));

        var installed = 0;
        foreach (var entry in viewModel.Versions)
        {
            if (!entry.IsInstalled) continue;

            installed++;
            var target = entry;
            menu.Items.Add(new MenuItem
            {
                Header = $"{entry.Name}   ·  {entry.KindLabel}",
                IsCheckable = true,
                IsChecked = IsCurrent(viewModel, entry.Name),
                Command = new RelayCommandShim(() => viewModel.SelectedVersion = target),
            });
        }

        if (installed == 0)
            menu.Items.Add(new MenuItem { Header = "（还没装任何版本）", IsEnabled = false });

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "浏览全部版本…",
            Command = new RelayCommandShim(() => GoToVersionsPage()),
        });

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "新建实例…", Command = viewModel.CreateInstanceCommand });
        menu.Items.Add(new MenuItem
        {
            Header = "重命名当前实例…",
            Command = viewModel.RenameInstanceCommand,
        });
        menu.Items.Add(new MenuItem
        {
            Header = "删除当前实例…",
            Command = viewModel.DeleteInstanceCommand,
        });
        menu.Items.Add(new MenuItem { Header = "刷新版本列表", Command = viewModel.RefreshVersionsCommand });

        return menu;
    }

    /// <summary>跳到「版本」页（那边才有搜索、渠道筛选和完整清单）。</summary>
    private void GoToVersionsPage()
    {
        if (Window.GetWindow(this) is MainWindow window)
            window.ShowPage("versions");
    }

    /// <summary>菜单里的灰色分组标题（不可点，只用来分段）。</summary>
    private static MenuItem Section(string text) => new()
    {
        Header = text,
        IsEnabled = false,
        FontSize = 11,
        Opacity = 0.55,
    };

    private static bool IsCurrent(MainViewModel viewModel, string versionName)
        => string.Equals(versionName, viewModel.SelectedVersion?.Name, StringComparison.Ordinal);

    /// <summary>
    /// 把「点一下做一件事」包成 ICommand —— MenuItem.Command 要的是命令，不是 Click。
    /// </summary>
    /// <remarks>
    /// 用 Click 事件也行，但那种写法要把 lambda 挂在菜单项上，菜单项回收后容易留一坨引用。
    /// 这里统一走命令，<c>CanExecute</c> 恒真，真正的可用性判断在 ViewModel 里。
    /// </remarks>
    private sealed class RelayCommandShim(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e) => ViewModel?.ClearLog();
    private void OnCopyLogClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        try
        {
            Clipboard.SetText(viewModel.LogText);
            viewModel.AppendLog($"已复制 {viewModel.LogText.Length} 个字符到剪贴板");
        }
        catch (Exception ex)
        {
            // 剪贴板被别的程序独占时会抛，这种情况不该崩
            viewModel.AppendLog($"[日志] 复制失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnOpenLogFileClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        var path = viewModel.LogFilePath;

        if (!File.Exists(path))
        {
            viewModel.AppendLog($"[日志] 文件还不存在：{path}");
            return;
        }

        try
        {
            // UseShellExecute 才会交给系统默认程序（记事本）打开
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            viewModel.AppendLog($"[日志] 打不开文件：{ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(
                $"打不开日志文件：\n\n{ex.Message}\n\n路径：\n{path}",
                "打不开",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
