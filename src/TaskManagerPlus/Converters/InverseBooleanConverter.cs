using System.Globalization;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>Plain boolean negation - used where a "Daily" radio button needs to reflect the
/// opposite of a single "IsWeekly" bool (suggestions.md #997) rather than adding a second,
/// redundant persisted property.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : value;
}
