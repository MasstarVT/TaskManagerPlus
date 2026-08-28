using System.Globalization;
using System.Windows.Data;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Converters;

/// <summary>#901: plain-English label for a Troubleshoot step's status - just adds a space to
/// "TimedOut" (the rest of the enum's names already read fine as-is).</summary>
public sealed class DiagnosticStepStatusToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            DiagnosticStepStatus.TimedOut => "Timed out",
            DiagnosticStepStatus.Pending => "Pending",
            DiagnosticStepStatus.Running => "Running…",
            DiagnosticStepStatus.Passed => "Passed",
            DiagnosticStepStatus.Warning => "Warning",
            DiagnosticStepStatus.Failed => "Failed",
            DiagnosticStepStatus.Skipped => "Skipped",
            _ => value?.ToString() ?? string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
