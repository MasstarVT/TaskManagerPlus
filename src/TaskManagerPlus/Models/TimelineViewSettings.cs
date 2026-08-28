namespace TaskManagerPlus.Models;

/// <summary>
/// #948: persisted Timeline panel view state - per-lane visibility plus the active date-range
/// preset and #944's correlation lookback window - so returning to the Timeline panel doesn't
/// reset every filter. Fails silently to "all lanes visible, 7-day default" on a missing/corrupt
/// file, the same shape every other settings file in this app uses (see AppPaths' remarks).
/// </summary>
public sealed class TimelineViewSettings
{
    public bool ShowCrashes { get; set; } = true;
    public bool ShowServiceFailures { get; set; } = true;
    public bool ShowWindowsUpdates { get; set; } = true;
    public bool ShowDriverInstalls { get; set; } = true;
    public bool ShowSoftwareInstalls { get; set; } = true;
    public bool ShowThermalEvents { get; set; } = true;
    public bool ShowPerfSpikes { get; set; } = true;
    public bool ShowNotes { get; set; } = true;

    /// <summary>One of "24h", "7d", "30d", "90d", "all" - see TimelineViewModel.ResolveWindow.</summary>
    public string RangePreset { get; set; } = "7d";

    /// <summary>#944: lookback window (hours) either side of a crash/failure marker used to count
    /// nearby change-lane markers - adjustable, defaults to 48h per the suggestion text.</summary>
    public double CorrelationWindowHours { get; set; } = 48;

    public static TimelineViewSettings Defaults => new();
}
