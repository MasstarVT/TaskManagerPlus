using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>Null -&gt; Collapsed, non-null -&gt; Visible. Used to show a section only once its
/// backing data (e.g. a computed snapshot diff) has actually been produced (#94).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
