using System.Globalization;
using System.Windows.Data;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.Converters;

/// <summary>#469: decodes a device's raw ConfigManagerErrorCode (int) into "Name"/"Cause"/
/// "NextStep" text via ProblemCodeDecoder - which piece is picked via ConverterParameter ("Cause"
/// or "NextStep"; anything else, including no parameter, gives the short Name). Mirrors
/// BugcheckCodeToDescriptionConverter's own "raw value on the model, lookup in a converter" shape
/// for a different code lookup, so PnpDeviceNode itself stays a plain data class with no Services
/// dependency.</summary>
public sealed class ProblemCodeToDescriptionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int code = value is int i ? i : 0;
        return (parameter as string)?.ToLowerInvariant() switch
        {
            "cause" => ProblemCodeDecoder.DescribeCause(code),
            "nextstep" => ProblemCodeDecoder.DescribeNextStep(code),
            _ => ProblemCodeDecoder.DescribeName(code),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
