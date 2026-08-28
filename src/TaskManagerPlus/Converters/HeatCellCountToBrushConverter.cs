using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskManagerPlus.Converters;

/// <summary>#130: colors one error-density heatmap cell by its Critical/Error count - a fixed
/// staircase of intensities (rather than a per-scan normalized 0-1 scale) so a single unusually
/// loud day doesn't wash out every other cell to near-invisible, and so the same count always reads
/// the same color across different scans/machines.</summary>
public sealed class HeatCellCountToBrushConverter : IValueConverter
{
    private static readonly Brush Zero = new SolidColorBrush(Color.FromArgb(0x18, 0x9A, 0x9A, 0xA2));
    private static readonly Brush Low = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xB8, 0x00));
    private static readonly Brush Medium = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0x8A, 0x00));
    private static readonly Brush High = new SolidColorBrush(Color.FromArgb(0xD0, 0xE8, 0x11, 0x23));
    private static readonly Brush VeryHigh = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));

    static HeatCellCountToBrushConverter()
    {
        Zero.Freeze(); Low.Freeze(); Medium.Freeze(); High.Freeze(); VeryHigh.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = value switch
        {
            int i => i,
            double d => (int)d,
            _ => 0,
        };

        return count switch
        {
            <= 0 => Zero,
            1 => Low,
            2 or 3 => Medium,
            4 or 5 or 6 => High,
            _ => VeryHigh,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
