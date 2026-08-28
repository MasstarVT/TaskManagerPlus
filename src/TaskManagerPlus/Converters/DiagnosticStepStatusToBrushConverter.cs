using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Converters;

/// <summary>#901: color-codes a Troubleshoot step's status dot/text - green Passed, amber
/// Warning/TimedOut, red Failed, muted gray Pending/Skipped, accent blue while Running.</summary>
public sealed class DiagnosticStepStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            DiagnosticStepStatus.Passed => "SuccessBrush",
            DiagnosticStepStatus.Warning => "WarningBrush",
            DiagnosticStepStatus.TimedOut => "WarningBrush",
            DiagnosticStepStatus.Failed => "DangerBrush",
            DiagnosticStepStatus.Running => "AccentBrush",
            DiagnosticStepStatus.Skipped => "TextTertiaryBrush",
            _ => "TextTertiaryBrush",
        };
        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
