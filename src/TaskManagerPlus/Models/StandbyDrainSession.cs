namespace TaskManagerPlus.Models;

/// <summary>#657: one measured overnight/standby drain session - battery percentage at sleep entry
/// and at resume, paired via the same Power-Troubleshooter event-1 Sleep Time/Wake Time timestamps
/// #654 already reads, with each side's battery percentage looked up from the nearest sample in
/// PowerHistoryLogService's existing once-a-minute persisted trail (extended to also carry battery
/// percent - see PowerTempSample.BatteryPercent). Persisted to standby-drain.json
/// (StandbyDrainService) so the trend is visible across app restarts, not just this session.</summary>
public sealed class StandbyDrainSession
{
    public DateTime SleepTime { get; init; }
    public DateTime WakeTime { get; init; }
    public double SleepBatteryPercent { get; init; }
    public double WakeBatteryPercent { get; init; }

    public double HoursAsleep => Math.Max(0.01, (WakeTime - SleepTime).TotalHours);
    public double DrainPercent => Math.Max(0, SleepBatteryPercent - WakeBatteryPercent);
    public double DrainPercentPerHour => DrainPercent / HoursAsleep;

    /// <summary>Modern Standby is generally expected to stay roughly at or under this - see
    /// EnergyThermalsViewModel.RefreshStandbyDrainSummary for how this is surfaced. Not a hard spec
    /// Microsoft publishes for every device, just a commonly-cited rough reference point.</summary>
    public const double HealthyReferencePercentPerHour = 2.0;
}
