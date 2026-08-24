using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TaskManagerPlus.Converters;

/// <summary>
/// Highlights the theme-mode picker button matching the currently active ThemeMode.
/// Values: [0] = this button's mode name, [1] = ThemeViewModel.ThemeMode.
/// </summary>
public sealed class ThemeModeSelectedBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool selected = values.Length == 2 && Equals(values[0], values[1]);
        var key = selected ? "AccentMutedBrush" : "BgElevatedBrush";
        return Application.Current.Resources[key] as Brush ?? Brushes.Transparent;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
