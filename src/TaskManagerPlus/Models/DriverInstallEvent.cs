namespace TaskManagerPlus.Models;

/// <summary>
/// #462: one device-install/update/configure section parsed out of %windir%\INF\setupapi.dev.log -
/// see DriverInstallLogService for the log's own section-delimited format
/// (&gt;&gt;&gt; header / &gt;&gt;&gt; Section start / &lt;&lt;&lt; Section end / &lt;&lt;&lt; [Exit status: ...]).
/// Category is the section's own label (e.g. "Device Install (Hardware initiated)", "Device Update",
/// "Preinstall"); Target is usually the device instance ID that section applies to. Not every field
/// is guaranteed present in every Windows/driver-install-tooling version - this is a best-effort,
/// adaptive read of a real but undocumented-schema log file, the same tier as
/// BootPerformanceService's own event-field extraction.
/// </summary>
public sealed class DriverInstallEvent
{
    public DateTime Timestamp { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string ExitStatus { get; init; } = string.Empty;
    public bool IsFailure { get; init; }
}

/// <summary>
/// #463 (setupapi.dev.log half): one DriverInstallEvent whose section logged a "!!!" error line or
/// a non-SUCCESS exit status - the same records as DriverInstallEvent, filtered down to just the
/// failures and carrying the actual error text/code. ErrorCode is a best-effort 0x-hex extraction
/// from the error/exit-status text (setupapi.dev.log's error lines aren't a documented, versioned
/// format) - null when no hex code pattern was found, shown as "Unknown" rather than guessed.
/// </summary>
public sealed class DriverInstallFailure
{
    public DateTime Timestamp { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string? ErrorCode { get; init; }
    public string ErrorText { get; init; } = string.Empty;
}

/// <summary>
/// #463 (event-log half): one Microsoft-Windows-Kernel-PnP/Configuration entry in the 400-series
/// range (411/442 explicitly called out by the suggestion, plus any other 400-499 entry from that
/// channel) - device configuration/install failures logged by the kernel PnP manager itself, a
/// separate source from setupapi.dev.log's own user-mode install log. This channel is disabled by
/// default on many systems (an analytic-style channel, not always turned on) - see
/// EventLogService.ReadPnpConfigurationFailures for how that's handled (degrades to "none found").
/// </summary>
public sealed class PnpConfigurationFailure
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string Level { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// #464: a boot-start/system-start driver that failed to load, from either the Service Control
/// Manager (System log events 7000/7001 - "the X service failed to start", and 7026 - "the
/// following boot-start or system-start driver(s) failed to load: ...") or the kernel PnP manager's
/// event 219 ("the driver ... failed to load for the device ..."). DriverName is a best-effort
/// regex extraction from the event's own formatted message (see
/// EventLogService.ReadBootDriverLoadFailures) - null when the message didn't match the expected
/// shape, shown as "Unknown" rather than guessed. Read independently by both the Devices &amp;
/// Drivers tab and the Stability tab's own snapshot (StabilitySnapshot.BootDriverLoadFailures), the
/// same "read twice, no ViewModel-to-ViewModel coupling" pattern EventLogService already uses for
/// ReadCorrectedMemoryErrors/ReadMemoryDiagnosticResult.
/// </summary>
public sealed class BootDriverLoadFailure
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string ProviderName { get; init; } = string.Empty;
    public string? DriverName { get; init; }
    public string Message { get; init; } = string.Empty;
}
