namespace TaskManagerPlus.Models;

/// <summary>One Winlogon notification-subscriber timing (#715) - GPClient/Profiles/TermSrv/Sens
/// and any other subscriber Winlogon notifies during sign-in, paired from the start/stop event
/// pair in Microsoft-Windows-Winlogon/Operational. Field names aren't a documented, versioned
/// schema (same adaptive-read tradeoff as BootPerformanceService.ExtractBootTimeFields), so
/// SubscriberName/SessionId are read by searching each event's Data fields by name rather than a
/// fixed index. See LogonDiagnosticsService.ReadSubscriberTimings.</summary>
public sealed class LogonSubscriberTiming
{
    public string SubscriberName { get; init; } = string.Empty;
    public string? SessionId { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime StopTime { get; init; }
    public double DurationMs => Math.Max(0, (StopTime - StartTime).TotalMilliseconds);
}

/// <summary>One Group Policy processing-time sample (#716) - Microsoft-Windows-GroupPolicy/
/// Operational event 8000 (computer boot policy) or 8001 (user logon policy), each of which
/// reports its own total elapsed processing time. Charted per-boot alongside the boot-time trend
/// so "my logon takes 90 seconds" can be attributed to policy processing when that's the cause.</summary>
public sealed class GroupPolicyProcessingEntry
{
    public DateTime TimeCreated { get; init; }
    public bool IsUserPolicy { get; init; }
    public int ElapsedMs { get; init; }

    public string Label => IsUserPolicy ? "User logon policy" : "Computer boot policy";
}

/// <summary>One Group Policy client-side extension's completion time (#717) - event 5016
/// (informational completion) or 6336/7016 (a slow/warned extension), each naming the extension
/// (Drive Maps, Group Policy Scripts, Folder Redirection, Registry, ...) and its elapsed
/// milliseconds. Ranked under the GP processing-time chart so "Drive Maps: 41,800 ms" is visible
/// rather than folded into a bare total.</summary>
public sealed class GroupPolicyCseEntry
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string ExtensionName { get; init; } = string.Empty;
    public int ElapsedMs { get; init; }

    public bool WasSlowOrWarned => EventId is 6336 or 7016;
}

/// <summary>Synchronous foreground policy audit (#718) - the handful of policy values under
/// HKLM\SOFTWARE\Policies\Microsoft\Windows\System that force the desktop to wait on policy/
/// scripts before sign-in completes, the classic "domain PC takes 3 minutes to sign in" cause.
/// Every field is nullable (Unknown) when the value isn't configured at all - an unset policy
/// value is a real, common, and entirely normal state, not a failure to read.</summary>
public sealed class SyncForegroundPolicyAudit
{
    public int? SyncForegroundPolicy { get; init; }
    public int? GpNetworkStartTimeoutPolicyValue { get; init; }
    public int? RunLogonScriptSync { get; init; }
    public int? DelayedDesktopSwitchTimeout { get; init; }

    public bool AnyForcesSynchronousWait =>
        SyncForegroundPolicy == 1 || RunLogonScriptSync == 1 || (GpNetworkStartTimeoutPolicyValue is > 0);

    public string SyncForegroundPolicyText => SyncForegroundPolicy switch
    {
        null => "Not configured (default: asynchronous after the first logon on this PC).",
        0 => "Disabled - policy is applied asynchronously, sign-in doesn't wait for it.",
        1 => "Enabled - Windows waits for policy processing to finish before the desktop appears on every sign-in.",
        var v => $"Unrecognized value ({v}).",
    };

    public string GpNetworkStartTimeoutText => GpNetworkStartTimeoutPolicyValue switch
    {
        null => "Not configured (default: 30-second wait for network availability before falling back to cached credentials).",
        var v => $"{v} second(s) - how long sign-in waits for network availability before falling back to cached credentials/policy.",
    };

    public string RunLogonScriptSyncText => RunLogonScriptSync switch
    {
        null => "Not configured (default: logon scripts run asynchronously, hidden, without blocking the desktop).",
        0 => "Disabled - logon scripts run asynchronously.",
        1 => "Enabled - the desktop waits for all logon scripts to finish running before it appears.",
        var v => $"Unrecognized value ({v}).",
    };

    public string DelayedDesktopSwitchTimeoutText => DelayedDesktopSwitchTimeout switch
    {
        null => "Not configured (default: 30 seconds).",
        var v => $"{v} second(s) - how long Windows delays switching to the interactive desktop for background policy processing.",
    };
}

/// <summary>One logon/startup/logoff/shutdown script found by the local Group Policy scan, or the
/// legacy per-user HKCU\Environment\UserInitMprLogonScript value (#719). Existence and
/// last-modified time are read straight off the file system; a configured path that no longer
/// resolves is flagged rather than silently skipped, since a missing script is itself often the
/// actual cause of a logon delay/failure (the client waits for a script that can never run).</summary>
public sealed class LogonScriptInfo
{
    public string Category { get; init; } = string.Empty; // "Machine Startup", "Machine Shutdown", "User Logon", "User Logoff", "Legacy logon script"
    public string Path { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public DateTime? LastModifiedUtc { get; init; }

    public string StatusText => Exists ? "Found" : "Configured, but not found on disk";
}
