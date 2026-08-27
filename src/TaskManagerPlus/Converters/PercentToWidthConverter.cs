using System.Globalization;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>Maps a 0-100 percent value to a pixel width within a fixed-width bar container -
/// used by the CPU tab's turbo-boost histogram bars (Round 8 #27). ConverterParameter is the
/// container's max width in pixels (defaults to 200 if omitted/unparsable).</summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double percent = value is double d ? d : 0;
        double max = parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : 200;
        return Math.Max(0, Math.Min(max, percent / 100.0 * max));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
