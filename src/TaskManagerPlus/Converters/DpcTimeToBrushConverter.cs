using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskManagerPlus.Converters;

/// <summary>#202: color-bands a DPC execution time in microseconds for the Responsiveness tab's
/// headline meter - green under 250us, amber under 1000us (LatencyMon's own commonly-cited
/// thresholds for "fine" vs. "worth investigating"), red at or above 1000us (audio-glitch
/// territory). Same three-brush shape as TemperatureToBrushConverter.</summary>
public sealed class DpcTimeToBrushConverter : IValueConverter
{
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x00));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));
    private static readonly Brush Unknown = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x72));

    static DpcTimeToBrushConverter()
    {
        Good.Freeze(); Warn.Freeze(); Bad.Freeze(); Unknown.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double? us = value switch
        {
            double d => d,
            float f => f,
            _ => null,
        };
        if (us is not double v) return Unknown;

        return v switch
        {
            >= 1000 => Bad,
            >= 250 => Warn,
            _ => Good,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
