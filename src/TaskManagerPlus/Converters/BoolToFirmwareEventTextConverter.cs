using System.Globalization;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>#602: renders a FirmwareThrottleEvent.IsRecovery flag as its plain-English event
/// description (event 37 vs. its 38 recovery counterpart).</summary>
public sealed class BoolToFirmwareEventTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? "Firmware limit lifted (event 38)"
            : "Firmware is limiting processor speed (event 37)";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
