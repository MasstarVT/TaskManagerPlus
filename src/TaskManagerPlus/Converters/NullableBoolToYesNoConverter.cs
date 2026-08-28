using System.Globalization;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>#688: renders a nullable bool (HDR supported/enabled, wide color gamut) as "Yes"/"No"/
/// "Unknown" - distinct from a plain BoolToVisibilityConverter-style true/false, since these fields
/// are genuinely three-valued (a target this app couldn't query at all is "Unknown", not "No").</summary>
public sealed class NullableBoolToYesNoConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch { true => "Yes", false => "No", _ => "Unknown" };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
