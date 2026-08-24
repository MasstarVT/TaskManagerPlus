using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskManagerPlus.Converters;

/// <summary>Color-codes a 0-100 percentage: green when idle, amber when busy, red when maxed out.</summary>
public sealed class PercentToBrushConverter : IValueConverter
{
    private static readonly Brush Low = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush Medium = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x00));
    private static readonly Brush High = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));

    static PercentToBrushConverter()
    {
        Low.Freeze(); Medium.Freeze(); High.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double percent = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0,
        };

        return percent switch
        {
            >= 85 => High,
            >= 60 => Medium,
            _ => Low,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
