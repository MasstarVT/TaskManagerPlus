using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskManagerPlus.Converters;

/// <summary>
/// Turns a 0-100 percent value plus an accent brush into a hard-edged, segmented
/// "LED bar" gradient brush (VfdMeter's retro readout), instead of a continuous
/// ProgressBar fill. Cheaper to render at the density a per-core grid needs than
/// an ItemsControl of individual segment Rectangles.
/// </summary>
public sealed class SegmentedMeterBrushConverter : IMultiValueConverter
{
    private const int SegmentCount = 16;
    private const double GapFraction = 0.16; // fraction of each segment's width left unlit as a gap

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double percent = values.Length > 0 && values[0] is double d ? d : 0.0;
        var accent = values.Length > 1 && values[1] is SolidColorBrush b ? b.Color : Colors.Gray;

        percent = Math.Clamp(percent, 0.0, 100.0);
        int litSegments = (int)Math.Round(percent / 100.0 * SegmentCount);

        var lit = accent;
        var unlit = Color.FromArgb(0x30, accent.R, accent.G, accent.B);

        var gradient = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 0) };
        double segmentWidth = 1.0 / SegmentCount;

        for (int i = 0; i < SegmentCount; i++)
        {
            double start = i * segmentWidth;
            double end = start + segmentWidth * (1.0 - GapFraction);
            double gapEnd = start + segmentWidth;
            var color = i < litSegments ? lit : unlit;

            gradient.GradientStops.Add(new GradientStop(color, start));
            gradient.GradientStops.Add(new GradientStop(color, end));
            gradient.GradientStops.Add(new GradientStop(Colors.Transparent, end));
            gradient.GradientStops.Add(new GradientStop(Colors.Transparent, gapEnd));
        }

        gradient.Freeze();
        return gradient;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
