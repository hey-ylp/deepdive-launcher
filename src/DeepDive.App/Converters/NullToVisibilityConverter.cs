using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DeepDive.App.Converters;

/// <summary>
/// 「是 null → 隐藏」，配合 <c>ConverterParameter=inverse</c> 反过来用（null 才显示）。
/// </summary>
/// <remarks>
/// 专门给「头像有没有加载出来」这类场景：头像是 <c>ImageSource?</c>，
/// 用 <see cref="BoolToVisibilityConverter"/> 得先在外面套一个 bool 属性，
/// 多绕一层还容易忘更新通知。
/// </remarks>
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>XAML 里用 {x:Static conv:NullToVisibilityConverter.Instance} 取单例。</summary>
    public static NullToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null;

        var inverse = parameter is string text
                      && text.Equals("inverse", StringComparison.OrdinalIgnoreCase);

        var show = inverse ? isNull : !isNull;

        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("这是单向绑定用的转换器");
}
