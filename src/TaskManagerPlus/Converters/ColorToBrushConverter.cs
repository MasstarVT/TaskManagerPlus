using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskManagerPlus.Converters;

/// <summary>Wraps a Color in a (frozen) SolidColorBrush for use as a Background/Fill binding.</summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color color) return Brushes.Transparent;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SolidColorBrush brush ? brush.Color : Colors.Transparent;
}
