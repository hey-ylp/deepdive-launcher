using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeepDive.App.ViewModels;
using Wpf.Ui.Controls;

namespace DeepDive.App;

/// <summary>
/// 主窗口：左侧导航 + 右侧页面 + 底部状态栏。
/// </summary>
/// <remarks>
/// 页面内容在 <c>Views/Pages/</c> 下，各自是 <see cref="System.Windows.Controls.UserControl"/>，
/// 共用同一个 <see cref="MainViewModel"/> 作 DataContext（UserControl 会从父级继承，不用手动给）。
/// </remarks>
public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        FitToWorkArea();

        Loaded += OnFirstLoaded;
    }

    /// <summary>
    /// 启动淡入：Loaded 时先把内容压成透明（此时窗口尚未呈现，用户看不见），
    /// 等 ContentRendered 再放动画。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不直接用 XAML 的 EventTrigger 挂在 Loaded 上</b>：这台机器实测
    /// <c>Show()</c> 返回 609ms、首次真正呈现 831ms，而 Loaded 在 582ms 就触发了。
    /// 动画挂 Loaded 会在 702ms 结束 —— 用户看到的第一帧已经是终态，动画等于不存在。
    /// </para>
    /// <para>
    /// <b>为什么要先设 Opacity=0 再等 ContentRendered</b>：ContentRendered 那一刻内容
    /// 已经渲染完了，如果这时候才把 Opacity 改 0，会先闪一帧完整界面再消失再淡入。
    /// 放在 Loaded 里改、渲染前就位，首帧就是空的灰窗口，不存在闪。
    /// </para>
    /// <para>
    /// <b>兜底</b>：ContentRendered 万一不触发（某些远程桌面 / 离屏场景会），
    /// 900ms 后强制归位，绝不能把界面锁死在透明状态。
    /// </para>
    /// </remarks>
    private void OnFirstLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;

        var shell = RootShell;
        if (shell is null) return;

        if (shell.RenderTransform is not TranslateTransform slide)
        {
            slide = new TranslateTransform();
            shell.RenderTransform = slide;
        }

        shell.Opacity = 0;
        slide.Y = 8;

        var done = false;

        void Play()
        {
            if (done) return;
            done = true;

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };

            var rise = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            };

            // FillBehavior.Stop：动画结束后属性回到基值。所以必须先把基值设成终态，
            // 否则动画一停就跳回 Opacity=0 —— 界面直接变没。
            shell.Opacity = 1;
            slide.Y = 0;

            shell.BeginAnimation(OpacityProperty, fade);
            slide.BeginAnimation(TranslateTransform.YProperty, rise);
        }

        ContentRendered += (_, _) => Play();

        // 兜底：ContentRendered 没来也必须让界面可见
        var guard = new DispatcherTimer(
            TimeSpan.FromMilliseconds(900),
            DispatcherPriority.Background,
            (_, _) => Play(),
            Dispatcher);

        guard.Start();
    }

    /// <summary>
    /// 把窗口尺寸夹到屏幕工作区内。
    /// </summary>
    /// <remarks>
    /// <para>
    /// XAML 里的 Width/Height 是<b>设备无关像素</b>。这台机器是 2560×1440 + 200% 缩放，
    /// 工作区只有约 1280×720 DIP —— 而窗口写的是 800 高，超出去 80，
    /// 结果<b>顶部被挤到屏幕外</b>：标题栏只露一半，「启动器」那行大字直接看不见，
    /// 日志框倒是完整的（因为它在下方）。看着像布局坏了，其实是尺寸问题。
    /// </para>
    /// <para>
    /// 不写死更小的尺寸：1080p 屏上 800 高用着更舒服。改成运行时按当前工作区夹一次，
    /// 换显示器、改缩放都不用动代码。
    /// </para>
    /// </remarks>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;

        // 留一点边，别贴着任务栏和屏幕边缘
        var margin = 24;

        if (Width > area.Width - margin)
            Width = Math.Max(MinWidth, area.Width - margin);

        if (Height > area.Height - margin)
            Height = Math.Max(MinHeight, area.Height - margin);

        if (MinHeight > area.Height - margin)
            MinHeight = Math.Max(480, area.Height - margin);
    }

    /// <summary>
    /// 从别的页面跳过来（比如启动页菜单里的「浏览全部版本…」）。
    /// </summary>
    /// <remarks>
    /// 直接拨导航按钮的 <c>IsChecked</c>，复用 <see cref="OnNavChecked"/> 那一套 ——
    /// 别在这里再写一份切页逻辑，两份早晚会不一致。
    /// </remarks>
    public void ShowPage(string tag)
    {
        var nav = tag switch
        {
            "versions" => NavVersions,
            "content" => NavContent,
            "tools" => NavTools,
            "lianji" => NavLianji,
            "settings" => NavSettings,
            _ => NavHome,
        };

        nav.IsChecked = true;
    }

    /// <summary>切页面。五个页面始终常驻（只切 <c>Visibility</c>），不销毁重建。</summary>
    /// <remarks>
    /// <para>
    /// <b>为什么常驻不重建</b>：切走再切回来时，重建会让日志的滚动位置、下拉框的选中状态
    /// 全部复位。而且每次重建都重新读一遍 mods 目录算推荐内存，没必要。
    /// </para>
    /// <para>
    /// <b>那个 null 判断不能省</b>：<c>InitializeComponent()</c> 是自上而下建控件的，
    /// <c>NavHome</c> 上写了 <c>IsChecked="True"</c>，所以 <c>Checked</c> 在解析到导航栏那一刻就触发了 ——
    /// 而此时下面那个 Grid 里的页面还没被创建，字段全是 null。
    /// 不判空会抛 <c>NullReferenceException</c>，表现为窗口构造函数里直接炸。
    /// </para>
    /// </remarks>
    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (PageHome is null) return;

        var key = (sender as FrameworkElement)?.Tag as string;

        PageHome.Visibility = key == "home" ? Visibility.Visible : Visibility.Collapsed;
        PageVersions.Visibility = key == "versions" ? Visibility.Visible : Visibility.Collapsed;
        PageContent.Visibility = key == "content" ? Visibility.Visible : Visibility.Collapsed;
        PageTools.Visibility = key == "tools" ? Visibility.Visible : Visibility.Collapsed;
        PageLianji.Visibility = key == "lianji" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = key == "settings" ? Visibility.Visible : Visibility.Collapsed;

        // 显式声明成 UIElement：各页是不同具体类型，switch 表达式找不到公共最佳类型（CS8506）
        UIElement? target = key switch
        {
            "versions" => PageVersions,
            "content" => PageContent,
            "tools" => PageTools,
            "lianji" => PageLianji,
            "settings" => PageSettings,
            _ => PageHome,
        };

        if (target is not null) PlayPageIn(target);
    }

    /// <summary>
    /// 切页时给目标页面一个「上浮淡入」，和启动动画同一套视觉语言。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么起始不是全透明</b>：页面是常驻的，切换只翻 <c>Visibility</c>，旧页会<b>瞬间</b>消失。
    /// 新页要是从 <c>Opacity=0</c> 开始，中间就有一段时间是纯灰底，看着像闪了一下。
    /// 从 0.18 起跳，既保留淡入的层次，又不会出现空档。
    /// </para>
    /// <para>
    /// <b>为什么基值也要设成起点</b>：<c>BeginAnimation</c> 不是立刻接管属性，要到下一个动画
    /// tick 才生效，这中间会照常渲染一帧。实测基值留在 1 时，切页瞬间会闪一帧完整界面
    /// 再跳回 0.18。基值同步压到起点，就没有这一帧。
    /// </para>
    /// <para>
    /// <b>为什么比启动动画短</b>：切页是高频操作，200ms 会显得黏。160ms 是能看清但不耽误事的下限。
    /// </para>
    /// <para>
    /// 用 <c>FillBehavior.HoldEnd</c> 停在终值，不用 Stop —— Stop 会让属性回落到那个 0.18 的基值，
    /// 动画一结束界面就变半透明。
    /// </para>
    /// </remarks>
    private static void PlayPageIn(UIElement page)
    {
        const double startOpacity = 0.18;
        const double startOffset = 10;
        var duration = TimeSpan.FromMilliseconds(160);

        if (page.RenderTransform is not TranslateTransform slide)
        {
            slide = new TranslateTransform();
            page.RenderTransform = slide;
        }

        page.Opacity = startOpacity;
        slide.Y = startOffset;

        var fade = new DoubleAnimation(startOpacity, 1, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };

        var rise = new DoubleAnimation(startOffset, 0, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };

        page.BeginAnimation(OpacityProperty, fade);
        slide.BeginAnimation(TranslateTransform.YProperty, rise);
    }
}
