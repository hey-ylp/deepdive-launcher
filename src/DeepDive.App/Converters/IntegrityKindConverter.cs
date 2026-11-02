using System.Globalization;
using System.Windows.Data;
using DeepDive.Core.Launcher;

namespace DeepDive.App.Converters;

/// <summary>
/// <see cref="IntegrityIssueKind"/> → 中文，给校验窗口的问题类型徽章用。
/// </summary>
public sealed class IntegrityKindConverter : IValueConverter
{
    /// <summary>XAML 里用 {x:Static conv:IntegrityKindConverter.Instance} 取单例。</summary>
    public static IntegrityKindConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is IntegrityIssueKind kind ? Name(kind) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static string Name(IntegrityIssueKind kind) => kind switch
    {
        IntegrityIssueKind.Missing => "缺失",
        IntegrityIssueKind.WrongSize => "大小不对",
        IntegrityIssueKind.WrongHash => "内容损坏",
        IntegrityIssueKind.EmptyFile => "空文件",
        IntegrityIssueKind.BrokenZip => "坏 jar",
        IntegrityIssueKind.IncompleteDownload => "没下完",
        _ => "问题",
    };
}
