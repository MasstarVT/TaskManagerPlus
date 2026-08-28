using System.Globalization;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>Non-empty string -&gt; true, null/empty -&gt; false. Used to show a section only once
/// its backing text has actually been produced (#213's session summary panel), the string
/// equivalent of NullToVisibilityConverter for a property that's never null, just empty by
/// default.</summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrEmpty(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
