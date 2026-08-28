namespace TaskManagerPlus.Models;

/// <summary>#952: the opt-in "capture a baseline automatically about once a week, while idle"
/// toggle plus its bookkeeping - persisted to baseline-settings.json (AppPaths.SettingsDirectory),
/// same shape/fail-silently convention as AlertThresholds/SummarySettings.</summary>
public sealed class BaselineSettings
{
    public bool AutoCaptureEnabled { get; set; }

    /// <summary>Days between automatic captures - fixed at 7 ("weekly") per #952's own wording, not
    /// currently user-configurable (unlike MaxBaselinesKept below, which #952 explicitly calls out
    /// as something to make configurable).</summary>
    public int IntervalDays { get; set; } = 7;

    /// <summary>#952: automatic pruning keeps only the most recent N baselines on disk (oldest
    /// deleted first) - manual captures count toward this same cap, since #951's regression
    /// comparison always wants "the oldest baseline still on disk", not "the oldest baseline ever
    /// captured".</summary>
    public int MaxBaselinesKept { get; set; } = 12;

    public DateTime? LastAutoCaptureUtc { get; set; }

    public static BaselineSettings Defaults => new();
}
