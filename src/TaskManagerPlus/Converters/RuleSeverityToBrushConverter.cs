using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Converters;

/// <summary>#928: color-codes the Health Check card's severity chip - grey Info, blue Low, amber
/// Medium, red High. A converter (not a Style/DataTrigger comparing Value="Low" as a string)
/// because RuleSeverity is a real enum - same pattern ServiceStatusToBrushConverter already uses
/// for ServiceControllerStatus, another real enum, elsewhere in this app. Hardcoded colors (not
/// theme palette DynamicResource brushes) - the same "fixed status color regardless of theme
/// family" tradeoff PercentToBrushConverter/ServiceStatusToBrushConverter already make.</summary>
public sealed class RuleSeverityToBrushConverter : IValueConverter
{
    private static readonly Brush Info = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA2));
    private static readonly Brush Low = new SolidColorBrush(Color.FromRgb(0x3C, 0x9E, 0xE8));
    private static readonly Brush Medium = new SolidColorBrush(Color.FromRgb(0xF5, 0xB9, 0x42));
    private static readonly Brush High = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));

    static RuleSeverityToBrushConverter()
    {
        Info.Freeze(); Low.Freeze(); Medium.Freeze(); High.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RuleSeverity.Low => Low,
        RuleSeverity.Medium => Medium,
        RuleSeverity.High => High,
        _ => Info,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
