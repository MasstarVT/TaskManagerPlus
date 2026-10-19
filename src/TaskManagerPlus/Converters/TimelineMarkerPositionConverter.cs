using System.Globalization;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>
/// #938: positions one Timeline marker along its lane's horizontal track - a MultiBinding over
/// the marker's own Timestamp, TimelineViewModel's WindowStartLocal/WindowEndLocal, and the lane
/// ItemsControl's own rendered ActualWidth (see TimelineView.xaml; 900 is only a fallback for an
/// unset value). Lane Events aren't date-filtered (only FilteredEvents is), so a marker outside the
/// resolved window is clamped to 0/track width rather than positioned outside the Canvas.
/// </summary>
public sealed class TimelineMarkerPositionConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 4) return 0.0;
        if (values[0] is not DateTime timestamp) return 0.0;
        if (values[1] is not DateTime start) return 0.0;
        if (values[2] is not DateTime end) return 0.0;
        double width = values[3] is double w ? w : 900.0;

        double totalTicks = (end - start).Ticks;
        if (totalTicks <= 0) return 0.0;

        double fraction = (timestamp - start).Ticks / totalTicks;
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        return fraction * width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
