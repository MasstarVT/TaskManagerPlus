namespace TaskManagerPlus.Models;

/// <summary>#656: one Kernel-Power event 42 ("entering sleep") that either never found a matching
/// resume within the lookback window, or resumed within seconds (a connected-standby session that
/// aborted almost immediately) - both point at a driver veto rather than a normal sleep/wake cycle.
/// Quick flag, not a verdict: a genuinely intentional near-instant wake (e.g. the user pressed a
/// key right after closing the lid) looks identical to a hardware-vetoed one from the event log
/// alone. See SleepVetoService.Correlate.</summary>
public sealed class SleepTransitionRecord
{
    public DateTime SleepAttemptTime { get; init; }

    /// <summary>Null when no resume was ever found for this sleep attempt within the lookback
    /// window - the "never came back" case.</summary>
    public DateTime? ResumeTime { get; init; }

    /// <summary>True for a resume that came back suspiciously fast (a few seconds) rather than
    /// genuinely never resuming.</summary>
    public bool WasImmediateAbort { get; init; }

    /// <summary>#650's top-ranked, repeated sleepstudy offender, attached as a general hint only
    /// when this session's own sleepstudy report has been loaded and found one - not a precise
    /// per-event attribution (the two data sources aren't correlated that closely).</summary>
    public string? PossibleVetoingDriverHint { get; init; }

    /// <summary>Same "compute the display sentence on the model itself" shape
    /// BatteryPresenceEvent.DescriptionText already uses, so the XAML side just binds one Run.</summary>
    public string DescriptionText => WasImmediateAbort
        ? $"Resumed after only {(ResumeTime!.Value - SleepAttemptTime).TotalSeconds:0}s - looks like an immediately-aborted connected-standby session."
        : "Never found a matching resume in the lookback window.";
}
