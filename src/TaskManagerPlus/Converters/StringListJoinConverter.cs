using System.Globalization;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>Joins an IReadOnlyList&lt;string&gt; into a comma-separated display string, for the
/// Services tab's dependency panel (#37) - "None" for an empty/null list.</summary>
public sealed class StringListJoinConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IReadOnlyList<string> list && list.Count > 0)
            return string.Join(", ", list);
        return "None";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
