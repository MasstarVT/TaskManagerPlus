using System.Globalization;
using System.Windows.Data;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.Converters;

/// <summary>#65: appends a plain-English name to a bugcheck hex code where BugcheckCodeLookup's
/// small table has one, for the Stability tab's minidump list - "Unknown" when the code itself is
/// null (unchanged from before this round).</summary>
public sealed class BugcheckCodeToDescriptionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BugcheckCodeLookup.Describe(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
