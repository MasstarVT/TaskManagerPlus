using System.Globalization;
using System.Windows.Data;
using LibreHardwareMonitor.Hardware;

namespace TaskManagerPlus.Converters;

/// <summary>
/// Maps a LibreHardwareMonitorLib SensorType to a short display unit. Needed specifically for
/// the Energy &amp; Thermals tab's Battery card, which - unlike the Temperatures/Fans/Voltages/
/// Wattages sections above it (each a single SensorType, so the unit is a fixed XAML string) -
/// mixes several sensor types (Level for charge %/degradation %, Voltage, Power for charge/
/// discharge rate) into one bucket, grouped by HardwareType instead. See EnergyThermalsViewModel.
/// </summary>
public sealed class SensorTypeToUnitConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SensorType type
            ? type switch
            {
                SensorType.Level => "%",
                SensorType.Voltage => "V",
                SensorType.Power => "W",
                SensorType.Current => "A",
                SensorType.Temperature => "°C",
                SensorType.TimeSpan => "s",
                _ => string.Empty,
            }
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
