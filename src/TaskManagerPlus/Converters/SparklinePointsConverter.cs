using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskManagerPlus.Converters;

/// <summary>#401: turns a process row's recent private-bytes samples (ProcessRow.MemorySparkline)
/// into a PointCollection for an inline Polyline sparkline in the Processes grid - min/max-
/// normalized to a fixed width/height so a flat, mostly-idle process still draws a visible (if
/// flat) line rather than one squashed by an unrelated process's much larger scale.</summary>
public sealed class SparklinePointsConverter : IValueConverter
{
    private const double Width = 56;
    private const double Height = 20;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyList<double> points || points.Count < 2)
            return new PointCollection();

        double min = points.Min();
        double max = points.Max();
        double range = max - min;

        var collection = new PointCollection(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
            double x = i / (double)(points.Count - 1) * Width;
            double normalized = range > 0 ? (points[i] - min) / range : 0.5;
            double y = Height - normalized * Height;
            collection.Add(new Point(x, y));
        }
        return collection;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
