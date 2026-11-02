using System.Windows;
using System.Windows.Controls;
using DeepDive.App.Models;
using DeepDive.App.ViewModels;
using Wpf.Ui.Controls;

namespace DeepDive.App.Views;

/// <summary>
/// 账号管理窗：列账号、切账号、登录微软账号、移除。
/// </summary>
/// <remarks>
/// 数据全在 <see cref="MainViewModel"/> 里 —— 这个窗口不持有任何状态，
/// 关掉再开还是同一个账号列表。它只负责把点击转成命令。
/// </remarks>
public partial class AccountWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;

    public AccountWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 绑的是 OneWay：用户点选后在这里主动告诉 VM。
        // 不用双向绑是因为 VM 里切换账号要连带写盘 + 刷头像，双向容易绕成环。
        if (AccountList.SelectedItem is AccountItemView item)
            _viewModel.SelectAccountCommand.Execute(item);
    }

    private void OnClientIdHelpClick(object sender, RoutedEventArgs e)
    {
        // Wpf.Ui.Controls 里也有一个 MessageBox，不写全名会 CS0104 二义
        System.Windows.MessageBox.Show(
            "这个框一般不用填，留空就能登录。\n\n" +
            "启动器内置了一个公共 client id（微软自家注册的 Xbox Live 应用），\n" +
            "实测能一路走通：微软登录取令牌 → Xbox Live → XSTS → Minecraft 服务。\n\n" +
            "登录流程：\n" +
            "1. 点「登录微软账号」，会自动弹出浏览器\n" +
            "2. 登录并点「是」同意授权\n" +
            "3. 页面最后跳到一片空白，把地址栏那条网址复制回来粘上\n" +
            "4. 点「完成登录」\n\n" +
            "登录成功后拿到的是 refresh token（90 天滚动续期），\n" +
            "之后每次启动如果令牌过期会自动换新的，不用再登。\n\n" +
            "什么时候需要自己填 id？\n" +
            "只有内置那个被封、或者你有自己的 Azure 应用时才需要。\n" +
            "注意：个人微软账号在 Azure 门户里进不了「应用注册」（会报 AADSTS16000），\n" +
            "所以这条路对个人基本是堵着的 —— 用默认的就好。",
            "关于 client id",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }
}
