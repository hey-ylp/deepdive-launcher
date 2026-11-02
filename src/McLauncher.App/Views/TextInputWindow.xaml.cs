using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace McLauncher.App.Views;

/// <summary>
/// 一个最小的「输入一行字」对话框 —— 建实例、改实例名要用。
/// </summary>
/// <remarks>
/// WPF 没有内置 InputBox（那个在 WinForms 里），为了两个场景去引 WinForms 不值当，
/// 干脆自己写一个。样式跟主窗口同一套 Fluent 资源，看起来不像野生的。
/// </remarks>
public partial class TextInputWindow : FluentWindow
{
    /// <summary>校验失败时由调用方设进来的一句话（比如「这个名字已经存在了」）。</summary>
    public string? ValidationMessage
    {
        get => HintText.Visibility == Visibility.Visible ? HintText.Text : null;
        set
        {
            HintText.Text = value ?? string.Empty;
            HintText.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>用户输入的内容（已去掉首尾空白）。</summary>
    public string Value => InputBox.Text.Trim();

    /// <summary>不带初始值的输入框。</summary>
    public TextInputWindow(string title, string prompt, string initialValue = "")
        : this(title, prompt, initialValue, hint: null)
    {
    }

    /// <param name="hint">输入框下面的灰字说明，比如「不能有 \ / : * ? 这些字符」。</param>
    public TextInputWindow(string title, string prompt, string initialValue, string? hint)
    {
        InitializeComponent();

        Title = title;
        WindowTitleBar.Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initialValue;

        if (!string.IsNullOrWhiteSpace(hint))
        {
            HintText.Text = hint;
            HintText.Visibility = Visibility.Visible;
        }

        // 打开就聚焦、全选，直接改名（不用先点一下输入框再 Ctrl+A）
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>
    /// 弹出并等结果。<paramref name="validate"/> 返回 false 表示「先别关，把 <paramref name="error"/> 显示出来」。
    /// </summary>
    /// <remarks>
    /// 校验放在这里而不是调完再弹错误框：名字不合法时用户不用重新点一遍「新建」再重输。
    /// </remarks>
    public static string? Ask(
        Window? owner,
        string title,
        string prompt,
        string initialValue = "",
        string? hint = null,
        Func<string, string?>? validate = null)
    {
        // ⚠️ 每次都要 new 一个新窗口：WPF 的 Window 关掉之后不能再 ShowDialog，
        // 循环复用会直接抛「窗口已关闭」。
        var seed = initialValue;

        while (true)
        {
            var window = new TextInputWindow(title, prompt, seed, hint)
            {
                Owner = owner ?? Application.Current?.MainWindow,
            };

            if (window.ShowDialog() != true)
                return null;

            var value = window.Value;
            var error = validate?.Invoke(value);
            if (error is null)
                return value;

            // 打回去重来，但把刚才输的内容带回去，别让人重打一遍
            seed = value;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text))
        {
            ValidationMessage = "不能空着。";
            InputBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnOkClick(sender, e);
            e.Handled = true;
        }
    }
}
