namespace TaskManagerPlus.Models;

/// <summary>
/// Round 12, #100: per-tab polling interval, in seconds - persisted to
/// %AppData%\TaskManagerPlus\poll-intervals.json (same shape as every other settings file),
/// letting a user on battery/low-power hardware trade update responsiveness for less background
/// CPU/WMI/perf-counter work. Defaults match every prior round's hardcoded intervals exactly, so
/// an existing install with no poll-intervals.json file behaves identically to before this file
/// existed.
///
/// Only covers the ViewModels that actually own a DispatcherTimer (per CLAUDE.md's Architecture
/// section): ProcessesViewModel, the shared PerformanceViewModel (which also drives the CPU/
/// Memory/Storage/Network thin-wrapper tabs - CLAUDE.md's "one shared sampler" model means there's
/// one interval knob for all four, not four independent ones), ServicesViewModel, and
/// EnergyThermalsViewModel. StartupViewModel/SystemSpecsViewModel/StabilityViewModel are
/// deliberately excluded - they're on-demand (initial load + manual Refresh, no timer at all, per
/// their own existing remarks), so there's no interval to make configurable.
/// </summary>
public sealed class PollIntervalSettings
{
    public double ProcessesSeconds { get; set; } = 1.0;
    public double PerformanceSeconds { get; set; } = 1.0;
    public double ServicesSeconds { get; set; } = 2.0;
    public double EnergyThermalsSeconds { get; set; } = 1.5;

    public static PollIntervalSettings Defaults => new();
}
