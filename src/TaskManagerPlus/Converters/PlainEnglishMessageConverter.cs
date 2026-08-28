using System.Globalization;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>suggestions.md #991: picks a Health Check finding's rendered message - values[0] is
/// HealthIssue.Message (the technical wording, always present), values[1] is
/// HealthIssue.PlainEnglishMessage (null unless the firing rule defined one), values[2] is
/// UiPreferences.PlainEnglishMode. Falls back to the technical Message whenever the plain-English
/// alternative isn't available for this particular finding, even with the toggle on - never a
/// blank line.</summary>
public sealed class PlainEnglishMessageConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        string message = values.Length > 0 && values[0] is string m ? m : string.Empty;
        string? plain = values.Length > 1 ? values[1] as string : null;
        bool plainEnglishMode = values.Length > 2 && values[2] is bool b && b;
        return plainEnglishMode && !string.IsNullOrEmpty(plain) ? plain : message;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
