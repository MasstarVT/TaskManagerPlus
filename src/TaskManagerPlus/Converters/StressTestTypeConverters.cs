using System.Globalization;
using System.Windows.Data;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.Converters;

/// <summary>#695-#700: StressTestType -&gt; its plain-English test name (StressTestReportService's
/// own DescribeType, reused here so the panel's RadioButton labels/history rows say exactly what
/// the exported report calls each test type).</summary>
public sealed class StressTestTypeDisplayNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is StressTestType t ? StressTestReportService.DescribeType(t) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Two-way "does this StressTestType property equal ConverterParameter" check, for driving
/// the panel's test-type RadioButton group off one enum property - same shape as IntEqualsConverter,
/// just for an enum instead of an int. ConverterParameter is the enum value's name as a string
/// (e.g. "CpuTorture").</summary>
public sealed class StressTestTypeEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is StressTestType v && parameter is string p &&
           Enum.TryParse<StressTestType>(p, out var target) && v == target;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is string p && Enum.TryParse<StressTestType>(p, out var target)
            ? target
            : Binding.DoNothing;
}
