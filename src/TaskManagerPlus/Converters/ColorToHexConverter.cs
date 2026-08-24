using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskManagerPlus.Converters;

/// <summary>Two-way converts between a Color and its "#RRGGBB" text for a hex entry box.</summary>
public sealed class ColorToHexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Color color ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : "#000000";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text) return Binding.DoNothing;
        text = text.Trim();

        try
        {
            var converted = ColorConverter.ConvertFromString(text);
            if (converted is Color color) return color;
        }
        catch
        {
            // Invalid hex - ignore the edit and keep the previous color.
        }
        return Binding.DoNothing;
    }
}
