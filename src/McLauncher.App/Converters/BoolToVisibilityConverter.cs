using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace McLauncher.App.Converters;

/// <summary>
/// bool → Visibility。用于「有加载器才显示徽章」这类显隐。
/// </summary>
/// <remarks>
/// 单例放在 <see cref="Instance"/>，同时在 <c>App.xaml</c> 里注册成 <c>BoolToVis</c> 资源。
/// 之所以两处都给：<c>StaticResource</c> 找不到 key 是<b>运行时</b>炸（XAML 编译期不查），
/// 之前校验窗口就是这么静默退出的。统一挂在 App 级资源里，任何窗口都能取到。
/// </remarks>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>XAML 里用 {x:Static conv:BoolToVisibilityConverter.Instance} 取单例。</summary>
    public static BoolToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;

        // ConverterParameter="inverse" 时取反：false 才显示
        if (string.Equals(parameter as string, "inverse", StringComparison.OrdinalIgnoreCase))
            flag = !flag;

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}
