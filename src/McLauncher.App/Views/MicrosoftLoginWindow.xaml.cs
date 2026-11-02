using System.Diagnostics;
using System.Windows;
using McLauncher.Core.Auth;
using Wpf.Ui.Controls;

namespace McLauncher.App.Views;

/// <summary>
/// 微软账号登录窗：v1 授权码流程。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是这套而不是设备码：</b>v2 的 <c>/consumers</c> 设备码端点我们根本用不了 ——
/// 自己注册的 Azure 应用（个人账号没有租户，AADSTS16000）和微软自家的 client id
/// （没注册在个人账户租户里，AADSTS700016）都死第一步。改用 v1 的
/// <c>login.live.com</c> 授权码，实测能一路走到 <c>login_with_xbox</c> 且拿得到 refresh token。
/// </para>
/// <para>
/// <b>为什么不用 WebView2：</b>那要多一个 100MB 的运行时依赖，且内嵌浏览器登录微软账号
/// 本身也不被鼓励。用系统默认浏览器 + 粘贴一次网址，零依赖、任何机器都能跑。
/// 代价是用户要复制一次地址栏 —— 只在新登录时发生一次，之后全靠 refresh token 自动续期。
/// </para>
/// <para>
/// 授权码<b>一次性、10 分钟过期</b>。失败基本都是重复用码或超时，重开一次授权页即可。
/// </para>
/// </remarks>
public partial class MicrosoftLoginWindow : FluentWindow
{
    private readonly MicrosoftAuthService _auth;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>授权页网址（含 client id 和 scope）。</summary>
    private readonly string _authorizeUrl;

    /// <summary>登录成功后拿到的账号。<c>null</c> = 没登成功。</summary>
    public MinecraftAccount? Result { get; private set; }

    /// <summary>这次实际用的 client id。</summary>
    public string UsedClientId { get; }

    /// <summary>失败时给调用方的简短说明。</summary>
    public string? FailureMessage { get; private set; }

    public MicrosoftLoginWindow(MicrosoftAuthService auth, string clientId)
    {
        InitializeComponent();

        _auth = auth;
        UsedClientId = string.IsNullOrWhiteSpace(clientId)
            ? MicrosoftAuthService.DefaultClientId
            : clientId.Trim();

        _authorizeUrl = MicrosoftAuthService.BuildAuthorizeUrl(UsedClientId);

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 用户点「登录」的意图本来就是要去登录，直接把授权页打开，省一次点击
        OpenInBrowser(_authorizeUrl);

        StatusText.Text = "已打开浏览器。登录、同意授权后，把最后那个空白页的网址复制到上面的框里。";

        UrlBox.Focus();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try { _cts.Cancel(); } catch { /* 已经取消过就算了 */ }
    }

    private void OpenInBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // 打不开不致命：地址本身也写在界面提示里，用户能自己复制
            StatusText.Text = $"浏览器没能自动打开（{ex.Message}），手动打开这个地址也行：{_authorizeUrl}";
        }
    }

    private void OnOpenBrowserClick(object sender, RoutedEventArgs e)
    {
        OpenInBrowser(_authorizeUrl);
        StatusText.Text = "授权页已打开。登录并点「是」之后，把跳转后的网址粘到上面的框里。";
    }

    /// <summary>
    /// 粘贴框内容一变就试着抠授权码 —— 抠到了就把「完成登录」点亮。
    /// </summary>
    private void OnUrlChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var code = MicrosoftAuthService.ExtractCode(UrlBox.Text);

        if (code is null)
        {
            SubmitButton.IsEnabled = false;
            ParseHint.Text = string.IsNullOrWhiteSpace(UrlBox.Text)
                ? "还没识别到授权码。"
                : "没认出来。要粘的是包含 code= 的整条网址（或者只粘 M. 开头的那串码）。";
            return;
        }

        SubmitButton.IsEnabled = !IsBusy;
        ParseHint.Text = $"已识别授权码（{code.Length} 字符），点右下角「完成登录」。";
    }

    private async void OnSubmitClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;

        var code = MicrosoftAuthService.ExtractCode(UrlBox.Text);

        if (code is null)
        {
            StatusText.Text = "没识别到授权码，重新粘贴一次。";
            return;
        }

        IsBusy = true;

        try
        {
            SetBusyUi(true);

            var account = await _auth.LoginWithCodeAsync(code, UsedClientId, _cts.Token);

            Result = account;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不算错误
        }
        catch (AuthException ex)
        {
            FailureMessage = ex.Hint;
            ShowFailure(ex.Hint, ex.Message);
        }
        catch (Exception ex)
        {
            FailureMessage = ex.Message;
            ShowFailure("登录过程中出了点意外，稍后重试。", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            SetBusyUi(false);
        }
    }

    private bool IsBusy { get; set; }

    private void SetBusyUi(bool busy)
    {
        SubmitButton.IsEnabled = !busy;
        OpenBrowserButton.IsEnabled = !busy;
        UrlBox.IsEnabled = !busy;

        StepTitle.Text = busy ? "正在登录…" : "两步完成登录";

        if (busy)
        {
            MessagePanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "正在用授权码换令牌，依次经过微软 → Xbox Live → Minecraft，大约几秒。";
        }
    }

    private void ShowFailure(string hint, string technical)
    {
        StepTitle.Text = "登录没成功";

        MessagePanel.Visibility = Visibility.Visible;
        MessageText.Text = hint;

        StatusText.Text = $"技术信息：{technical}";

        // 授权码已经废了，清空让用户重新走一遍
        UrlBox.Clear();
        SubmitButton.IsEnabled = false;
        ParseHint.Text = "重新点「打开授权页」，拿一条新的网址过来。";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        try { _cts.Cancel(); } catch { }

        DialogResult = false;
        Close();
    }
}
