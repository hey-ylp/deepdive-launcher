using System.Globalization;
using System.Windows.Data;

namespace McLauncher.App.Converters;

/// <summary>
/// 取字符串第一个字符，用于「没有头像时显示首字母」的占位格。
/// </summary>
/// <remarks>
/// 直接绑全名到 22px 见方的格子里会被截成 "Play" 之类，且紧挨着右边的完整名字，
/// 视觉上像是名字重复了。这个小写字母位只出一个字就够了。
/// </remarks>
public sealed class FirstLetterConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text)) return "?";

        var trimmed = text.Trim();
        var first = char.ToUpperInvariant(trimmed[0]);

        // 东亚字符不转大写，原样返回
        return first.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
