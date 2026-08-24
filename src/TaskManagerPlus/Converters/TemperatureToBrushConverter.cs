using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskManagerPlus.Converters;

/// <summary>Color-codes a Celsius temperature reading: green when cool, amber when warm, red
/// when hot. Thresholds are tuned for CPU/GPU package temps, not ambient/storage sensors, but
/// used generically across the Energy &amp; Thermals tab's temperature tiles for now.</summary>
public sealed class TemperatureToBrushConverter : IValueConverter
{
    private static readonly Brush Cool = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush Warm = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x00));
    private static readonly Brush Hot = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));
    private static readonly Brush Unknown = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x72));

    static TemperatureToBrushConverter()
    {
        Cool.Freeze(); Warm.Freeze(); Hot.Freeze(); Unknown.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double? celsius = value switch
        {
            double d => d,
            float f => f,
            _ => null,
        };

        if (celsius is not double c) return Unknown;

        return c switch
        {
            >= 85 => Hot,
            >= 70 => Warm,
            _ => Cool,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
