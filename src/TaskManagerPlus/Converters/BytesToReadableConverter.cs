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
        double bytes = value switch
        {
            long l => l,
            int i => i,
            double d => d,
            _ => 0,
        };

        bool isRate = string.Equals(parameter as string, "rate", StringComparison.OrdinalIgnoreCase);

        double abs = Math.Abs(bytes);
        int unitIndex = 0;
        while (abs >= 1024 && unitIndex < Units.Length - 1)
        {
            abs /= 1024;
            unitIndex++;
        }

        string formatted = unitIndex == 0
            ? $"{abs:0} {Units[unitIndex]}"
            : $"{abs:0.0} {Units[unitIndex]}";

        return isRate ? formatted + "/s" : formatted;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
