using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Converters;

/// <summary>#284: color-codes a background-activity ribbon indicator dot - green Active, gray
/// Inactive, dim gray Unknown (a missing WMI namespace/service/registry key, not "checked and
/// found idle" - deliberately dimmer than Inactive so it reads as "no data", matching
/// CLAUDE.md's "degrade to Unknown/0/hidden" convention). Same three-color shape as
/// ServiceStatusToBrushConverter, just keyed off BackgroundActivityState instead of
/// ServiceControllerStatus.</summary>
public sealed class BackgroundActivityStateToBrushConverter : IValueConverter
{
    private static readonly Brush Active = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
    private static readonly Brush Inactive = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x72));
    private static readonly Brush Unknown = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52));

    static BackgroundActivityStateToBrushConverter()
    {
        Active.Freeze(); Inactive.Freeze(); Unknown.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is BackgroundActivityState.Active ? Active
         : value is BackgroundActivityState.Inactive ? Inactive
         : Unknown;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
