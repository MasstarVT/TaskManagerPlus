using System.Globalization;
using System.Windows.Data;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Converters;

/// <summary>Round 13, item 3: plain-English label for a ShutdownCause value, for the "Unexpected
/// shutdowns" card's per-row list - see EventLogService.ClassifyPowerEvent's remarks on how
/// tentative this classification actually is ("quick flag, not a verdict" per CLAUDE.md).</summary>
public sealed class ShutdownCauseToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            ShutdownCause.Bugcheck => "Bugcheck (BSOD)",
            ShutdownCause.PowerButtonHeld => "Power button held",
            ShutdownCause.PowerLoss => "Looks like a power loss",
            ShutdownCause.HardHang => "Looks like a hard hang",
            _ => "Unknown",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
