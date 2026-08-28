namespace TaskManagerPlus.Models;

/// <summary>What kind of existing control surface this #896 cleanup candidate maps to - decides
/// which of this app's EXISTING toggle methods a Disable click on this row routes through
/// (ServiceControlService.SetStartupType / ScheduledTaskService.SetEnabledAsync /
/// StartupManagerService.SetEnabled). See OemCleanupService.</summary>
public enum OemCleanupKind
{
    Service,
    ScheduledTask,
    StartupItem,
}

/// <summary>
/// One #896 OEM cleanup candidate - a #895 bloatware inventory entry (or, for DiagTrack/CEIP, a
/// well-known Microsoft telemetry surface surfaced unconditionally per the item's own text) cross-
/// referenced against a currently installed service, scheduled task, or startup entry by a simple
/// name/publisher substring match. NEVER a delete action - Disable routes through the SAME
/// existing control method the Services/Startup tabs already use for that item's Kind.
/// </summary>
public sealed class OemCleanupCandidate
{
    /// <summary>The bloatware/telemetry item this candidate is "cleaning up after" - e.g. "Dell
    /// SupportAssist", or "Connected User Experiences and Telemetry" for the DiagTrack special case.</summary>
    public string SourceName { get; init; } = string.Empty;

    public OemCleanupKind Kind { get; init; }

    /// <summary>The exact service name / scheduled task name / startup item name this row acts
    /// on - passed straight through to the existing toggle method for Kind.</summary>
    public string TargetName { get; init; } = string.Empty;

    public string TargetDetail { get; init; } = string.Empty;

    public bool IsCurrentlyEnabled { get; init; }

    /// <summary>Only set for Kind==StartupItem - StartupManagerService.SetEnabled needs the whole
    /// StartupItem (Source/Name/Command), not just a name, so the exact instance from the fresh
    /// StartupManagerService.Sample() this candidate was matched against is carried through
    /// directly rather than re-resolved from a bare string.</summary>
    public StartupItem? StartupItemRef { get; init; }

    /// <summary>True for the DiagTrack service / CEIP scheduled tasks specifically - the UI shows
    /// the honest static-text note about what disabling these does and doesn't do next to rows
    /// carrying this flag, per the item's own text.</summary>
    public bool IsTelemetrySpecialCase { get; init; }

    public string KindLabel => Kind switch
    {
        OemCleanupKind.Service => "Service",
        OemCleanupKind.ScheduledTask => "Scheduled task",
        _ => "Startup item",
    };
}
