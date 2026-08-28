namespace TaskManagerPlus.Models;

/// <summary>
/// Which of the four causes #603's classifier attributes a throttled tick to - see
/// ThrottleClassificationService.Classify's remarks for exactly how each is distinguished. Same
/// "quick flag, not a verdict" tier as every other heuristic in this app, except Firmware, which
/// is corroborated by an authoritative Windows event (#602).
/// </summary>
public enum ThrottleReasonClass
{
    None,
    Thermal,
    Power,
    Firmware,
    CoreParked,
}

/// <summary>
/// One contiguous throttling episode (#604) - persisted to throttle-history.json
/// (ThrottleHistoryService) so the Energy &amp; Thermals tab's throttle list becomes a "has this
/// been getting worse over time" record instead of the 10 most-recent in-memory entries it was
/// before this round. Built by EnergyThermalsViewModel as it watches ThrottleClassificationService's
/// per-tick verdict transition from None to something else and back.
/// </summary>
public sealed class ThrottleEpisode
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }

    /// <summary>Highest CPU package temperature observed during the episode.</summary>
    public double PeakTempC { get; set; }

    /// <summary>Highest CPU package power draw observed during the episode.</summary>
    public double PeakPackagePowerW { get; set; }

    /// <summary>Mean effective clock (MHz) across the episode's samples.</summary>
    public double MeanEffectiveMhz { get; set; }

    public ThrottleReasonClass ReasonClass { get; set; }

    /// <summary>Seconds between a freshly tracked sustained-load period starting (CPU &gt; 80% for
    /// &gt; 15s, #605) and this episode's first sample - null when this episode didn't follow a
    /// freshly tracked sustained-load start (app launched mid-load, or the load dipped below 80%
    /// in between), since "time to throttle" only means something when measured from a clean
    /// load-start point.</summary>
    public double? TimeToThrottleSeconds { get; set; }
}
