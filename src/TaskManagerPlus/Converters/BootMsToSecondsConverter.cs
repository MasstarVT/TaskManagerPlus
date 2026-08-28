using System.Globalization;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>Formats a millisecond duration (int or int?) as "N.Ns" for the Startup tab's boot
/// time breakdown (#89) - null (no reading) renders as "–".</summary>
public sealed class BootMsToSecondsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        int ms => $"{ms / 1000.0:0.#}s",
        // #701/#702: culprit-board totals are summed as doubles (fractional milliseconds can
        // accumulate across many boots) - same formatting, just a wider source type.
        double ms => $"{ms / 1000.0:0.#}s",
        null => "–",
        _ => "–",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
