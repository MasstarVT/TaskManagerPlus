using System.Globalization;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>
/// Converts a byte count (long or double) to a human readable string, e.g. "482.3 MB".
/// Pass ConverterParameter="rate" to append "/s" for throughput values.
/// </summary>
public sealed class BytesToReadableConverter : IValueConverter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double? maybeBytes = value switch
        {
            long l => l,
            int i => i,
            double d => d,
            ulong ul => ul,
            uint ui => ui,
            float f => f,
            _ => null,
        };
        // A null (unreadable) value must not render as a real-looking "0 B".
        if (maybeBytes is not { } bytes || double.IsNaN(bytes) || double.IsInfinity(bytes)) return "Unknown";

        bool isRate = string.Equals(parameter as string, "rate", StringComparison.OrdinalIgnoreCase);

        double abs = Math.Abs(bytes);
        int unitIndex = 0;
        while (abs >= 1024 && unitIndex < Units.Length - 1)
        {
            abs /= 1024;
            unitIndex++;
        }

        // Keep the sign: deltas such as the Processes "WS/Priv gap" column are legitimately negative.
        // ...but not on a value that rounds to zero ("-0 B").
        double shown = Math.Round(abs, unitIndex == 0 ? 0 : 1);
        string sign = bytes < 0 && shown > 0 ? "-" : string.Empty;
        string formatted = unitIndex == 0
            ? $"{sign}{abs:0} {Units[unitIndex]}"
            : $"{sign}{abs:0.0} {Units[unitIndex]}";

        return isRate ? formatted + "/s" : formatted;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
