using System.Globalization;
using System.Windows.Data;

namespace DeepDive.App.Converters;

/// <summary>
/// bool 取反。用于「忙碌时禁用输入控件」这类绑定。
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    /// <summary>XAML 里用 {x:Static conv:InverseBoolConverter.Instance} 取单例。</summary>
    public static InverseBoolConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
