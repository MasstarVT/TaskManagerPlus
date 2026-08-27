using System.Globalization;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>Two-way "does this int property equal ConverterParameter" check, for driving a group
/// of RadioButtons off one int property (Round 11, #76's sample-interval selector) without a
/// separate command per option. ConvertBack only fires when the user checks a button (WPF never
/// raises it for the button being unchecked), so it's safe to always push the parameter back.</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && parameter is not null && int.TryParse(parameter.ToString(), out var p) && i == p;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is not null && int.TryParse(parameter.ToString(), out var p) ? p : Binding.DoNothing;
}
