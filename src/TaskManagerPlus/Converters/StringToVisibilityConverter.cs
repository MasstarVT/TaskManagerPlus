using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>Null/empty string -&gt; Collapsed, non-empty -&gt; Visible - round 10 (#301-#312)'s
/// several optional per-disk SMART summary lines (temperature, power-on, endurance, ...) are
/// plain strings that are empty until a disk is actually read, or when that particular attribute
/// isn't reported at all, rather than always-present placeholder text.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
