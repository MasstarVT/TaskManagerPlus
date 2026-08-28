using System.Globalization;
using System.Windows.Data;

namespace TaskManagerPlus.Converters;

/// <summary>Round 13, items 1/8: labels a minidump row's bugcheck data source - the authoritative
/// BugCheck 1001 record (true) vs. the WER-SystemErrorReporting 1001 fallback used only when the
/// BugCheck provider entry itself is missing (false). See EventLogService.ReadBugCheckRecords/
/// ReadWerSummaryBugChecks.</summary>
public sealed class BugcheckSourceToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Confirmed (BugCheck 1001)" : "WER-SystemErrorReporting summary (fallback)";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
