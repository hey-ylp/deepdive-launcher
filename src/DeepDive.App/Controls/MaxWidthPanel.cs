using System.Windows;
using System.Windows.Controls;

namespace DeepDive.App.Controls;

/// <summary>
/// 把内容限制在一个最大宽度内并水平居中，超出部分留白。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 <c>MaxWidth</c> + <c>HorizontalAlignment="Center"</c></b>：
/// 那两个一起用，WPF 在 Arrange 阶段会把宽度收成<b>内容的 DesiredSize</b>，
/// 而不是「可用宽度与上限的较小值」。首页那种「左右各贴一边、中间靠 * 撑开」的版式，
/// DesiredSize 远小于窗口宽度，结果整个页面缩成很窄的一条 —— 比不限制还难看。
/// </para>
/// <para>
/// 这个 Panel 自己做两件事：Measure 时把可用宽度砍到上限，Arrange 时算出居中偏移。
/// 于是窗口窄的时候它跟普通 Grid 没区别，全屏时才把内容框住。
/// </para>
/// </remarks>
public sealed class MaxWidthPanel : Panel
{
    /// <summary>内容区最大宽度（逻辑像素）。</summary>
    public static readonly DependencyProperty MaxContentWidthProperty = DependencyProperty.Register(
        nameof(MaxContentWidth),
        typeof(double),
        typeof(MaxWidthPanel),
        new FrameworkPropertyMetadata(1300d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>内容区最大宽度（逻辑像素）。</summary>
    public double MaxContentWidth
    {
        get => (double)GetValue(MaxContentWidthProperty);
        set => SetValue(MaxContentWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = MaxContentWidth;

        var width = double.IsInfinity(availableSize.Width)
            ? limit
            : Math.Min(availableSize.Width, limit);

        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        var childConstraint = new Size(width, height);

        foreach (UIElement child in InternalChildren)
            child.Measure(childConstraint);

        // 高度交回给父级（内容页要能吃掉剩余空间，日志卡才拉得开）
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Min(finalSize.Width, MaxContentWidth);
        var offset = Math.Max(0, (finalSize.Width - width) / 2);

        foreach (UIElement child in InternalChildren)
            child.Arrange(new Rect(offset, 0, width, finalSize.Height));

        return finalSize;
    }
}
