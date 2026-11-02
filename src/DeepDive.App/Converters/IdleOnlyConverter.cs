using System;
using System.Globalization;
using System.Windows.Data;
using DeepDive.Core.Launcher;

namespace DeepDive.App.Converters;

/// <summary>
/// <see cref="EasyTierState"/> → bool：只在 <c>Idle</c>（空闲）时返回 true。
/// </summary>
/// <remarks>
/// 联机页的「创建房间」「加入房间」按钮只有在没联机、没在启动/停止时才可点。
/// </remarks>
public sealed class IdleOnlyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is EasyTierState.Idle;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
