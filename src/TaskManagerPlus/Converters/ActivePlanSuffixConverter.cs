using System.Globalization;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>Round 12, #90: true -&gt; " (active)", false -&gt; "" - used to append an "active"
/// suffix onto a Run inline element, which (unlike a FrameworkElement) has no Visibility property
/// to toggle with a plain DataTrigger.</summary>
public sealed class ActivePlanSuffixConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? " (active)" : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
