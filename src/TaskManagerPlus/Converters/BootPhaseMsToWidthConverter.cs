using System.Globalization;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>#703: maps a boot-phase millisecond value (int or int?) onto a pixel width within the
/// Startup tab's stacked boot-phase bar - same "value / max * track width" shape as
/// PercentToWidthConverter, just scaled off a millisecond ceiling instead of a 0-100 percent.
/// ConverterParameter is the track's max width in pixels (defaults to 300). The scale is fixed at
/// 40 seconds full-track so the bar has headroom to show an over-target boot rather than clip at
/// the edge - see StartupView.xaml's healthy-boot reference markers, which are placed at the same
/// fixed scale (20s/30s cumulative) rather than computed from this converter.</summary>
public sealed class BootPhaseMsToWidthConverter : IValueConverter
{
    private const double MaxMs = 40000;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double ms = value switch { int i => i, double d => d, _ => 0 };
        double max = parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : 300;
        return Math.Max(0, Math.Min(max, ms / MaxMs * max));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
