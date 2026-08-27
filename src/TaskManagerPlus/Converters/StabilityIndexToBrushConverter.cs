using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskManagerPlus.Converters;

/// <summary>#68: color-codes the 0-10 stability index - the inverse direction of
/// PercentToBrushConverter (there, higher is worse; here, higher is better): green at 8+, amber at
/// 5-8, red below 5.</summary>
public sealed class StabilityIndexToBrushConverter : IValueConverter
{
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush Fair = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x00));
    private static readonly Brush Poor = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));

    static StabilityIndexToBrushConverter()
    {
        Good.Freeze(); Fair.Freeze(); Poor.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double score = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 10,
        };

        return score switch
        {
            >= 8 => Good,
            >= 5 => Fair,
            _ => Poor,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
