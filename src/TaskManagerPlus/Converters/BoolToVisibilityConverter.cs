using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>Plain bool-to-Visibility (true = Visible, false = Collapsed) - used by Round 11, #69's
/// dashboard tile hide toggle. A small hand-rolled converter rather than depending on WPF's own
/// System.Windows.Controls.BooleanToVisibilityConverter, matching this project's existing
/// preference for small, purpose-named converters over a built-in one whose namespace/behavior
/// isn't otherwise referenced anywhere in this codebase.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}
