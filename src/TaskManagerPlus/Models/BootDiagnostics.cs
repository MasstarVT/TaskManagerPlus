namespace TaskManagerPlus.Models;

/// <summary>One boot-start/system-start driver or service that failed to load, read from the
/// System log's Service Control Manager events (#708) - 7026 enumerates every boot/system-start
/// driver or service that didn't load this boot (one insertion string per name, no per-entry
/// detail), while 7000/7001 cover a single driver ("failed to start") or service ("a dependency
/// failed") respectively, each with its own formatted error text. See
/// BootPerformanceService.ReadDriverLoadFailures.</summary>
public sealed class DriverLoadFailure
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;

    public string CategoryText => EventId switch
    {
        7026 => "Did not load at boot",
        7000 => "Driver failed to start",
        7001 => "Service dependency failed",
        _ => "Load failure",
    };
}

/// <summary>Prefetcher/ReadyBoot configuration audit (#711) - reads
/// HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters
/// plus the SysMain service state. EnablePrefetcher/EnableSuperfetch are the documented DWORD
/// values Windows itself reads (0 = off, 1 = application-launch prefetching only, 2 = boot
/// prefetching only, 3 = both - the Windows default). Null means the key/value couldn't be read
/// (missing, access denied) - shown as Unknown, never assumed to be either default or disabled.
/// See PrefetchAuditService.Read.</summary>
public sealed class PrefetchAuditResult
{
    public int? EnablePrefetcher { get; init; }
    public int? EnableSuperfetch { get; init; }
    public string SysMainStatus { get; init; } = "Unknown";
    public string SystemDriveMediaType { get; init; } = "Unknown";

    /// <summary>True when boot-time prefetching specifically (bit value 2) is off - EnablePrefetcher
    /// is 0 or 1. Null (Unknown) when the value itself couldn't be read.</summary>
    public bool? BootPrefetchDisabled => EnablePrefetcher is null ? null : EnablePrefetcher is 0 or 1;

    /// <summary>"Quick flag, not a verdict" (see CLAUDE.md's cross-cutting conventions) - Microsoft
    /// itself recommends prefetch/Superfetch off on many modern SSD/NVMe boot drives, so this only
    /// reads as a likely-mistake on a spinning disk, the classic "a debloat guide told me to
    /// disable this" case.</summary>
    public bool LooksLikeMistake => BootPrefetchDisabled == true && SystemDriveMediaType == "HDD";

    public string SummaryText
    {
        get
        {
            string prefetcher = EnablePrefetcher switch
            {
                null => "Unknown",
                0 => "Disabled",
                1 => "App-launch prefetching only",
                2 => "Boot prefetching only",
                3 => "Enabled (default)",
                var v => $"Unrecognized value ({v})",
            };
            string superfetch = EnableSuperfetch switch
            {
                null => "Unknown",
                0 => "Disabled",
                3 => "Enabled (default)",
                var v => $"Value {v}",
            };
            return $"EnablePrefetcher: {prefetcher} · EnableSuperfetch: {superfetch} · SysMain service: {SysMainStatus} · System drive: {SystemDriveMediaType}";
        }
    }
}

/// <summary>Boot-data-unavailable explainer (#712) - when BootPerformanceService.ReadLatest()
/// returns null, this tells apart "the Diagnostics-Performance channel itself is disabled"
/// (fixable with one click) from "a WDI group policy appears to restrict a diagnostics scenario"
/// (fixable only via Group Policy) from "neither of those - genuinely no matching event found
/// yet." Every field is nullable/Unknown rather than guessed - see
/// BootPerformanceService.DiagnoseUnavailabilityAsync.</summary>
public sealed class BootDataAvailability
{
    public bool? ChannelEnabled { get; init; }
    public bool? PolicyLooksDisabled { get; init; }

    public string ExplanationText => (ChannelEnabled, PolicyLooksDisabled) switch
    {
        (false, _) => "The Microsoft-Windows-Diagnostics-Performance/Operational event log channel is disabled, so Windows isn't recording boot-time events at all.",
        (_, true) => "A WDI group policy appears to disable a diagnostics scenario - boot performance monitoring may be restricted by policy on this machine.",
        (true, _) => "The channel is enabled, but no matching boot event was found yet (a very recent install, or the event has aged out).",
        (null, _) => "Couldn't determine whether the diagnostics channel is enabled (access denied, or wevtutil unavailable).",
    };

    public bool CanOfferEnable => ChannelEnabled == false;
}

/// <summary>BootExecute audit (#714) - HKLM\SYSTEM\CurrentControlSet\Control\Session
/// Manager\BootExecute, compared against the single stock entry ("autocheck autochk *") every
/// clean Windows install has. Anything beyond that - an extra scheduled `autochk /r` on a volume
/// (chkdsk queued for next boot), or a third-party entry - both delays boot and is worth a look.
/// A plain-language reading, not a verdict - a legitimately queued chkdsk after a dirty unmount is
/// normal, just worth knowing about. See BootPerformanceService.ReadBootExecute.</summary>
public sealed class BootExecuteInfo
{
    public string[] RawValue { get; init; } = Array.Empty<string>();
    public bool IsStock { get; init; }
    public string Explanation { get; init; } = string.Empty;

    public string RawValueText => RawValue.Length == 0 ? "(empty)" : string.Join("; ", RawValue);
}

/// <summary>#709: pending state for the two-step, opt-in "Capture a boot log" workflow - persisted
/// under AppPaths.SettingsDirectory (same small-JSON-settings pattern as poll-intervals.json) so
/// it survives the restart the workflow itself requires. Never armed silently - only ever written
/// by an explicit "Capture a boot log" button click, and IsArmed is always visible/reversible from
/// the Startup tab before the user reboots. See BootLogCaptureService.</summary>
public sealed class BootLogCaptureState
{
    public bool IsArmed { get; set; }
    public DateTime? ArmedAtUtc { get; set; }
}

/// <summary>One line parsed out of %windir%\ntbtlog.txt (#709) - Windows writes one "Loaded
/// driver ..." or "Did not load driver ..." line per driver, in load order, when boot logging
/// (bcdedit /set {current} bootlog yes) is on.</summary>
public sealed class NtbtlogEntry
{
    public int Order { get; init; }
    public bool Loaded { get; init; }
    public string DriverPath { get; init; } = string.Empty;
}

/// <summary>Parsed result of a captured ntbtlog.txt (#709).</summary>
public sealed class NtbtlogResult
{
    public DateTime CapturedAtUtc { get; init; }
    public List<NtbtlogEntry> Entries { get; init; } = new();

    public List<NtbtlogEntry> FailedDrivers => Entries.Where(e => !e.Loaded).ToList();
}

/// <summary>#710: pending state for the two-step, opt-in boot ETW trace workflow (Windows
/// Performance Recorder's -boottrace mode) - same persisted-JSON/never-silently-armed shape as
/// BootLogCaptureState. OutputPath is decided at arm time so the collection step after reboot
/// knows exactly where to write the .etl. See BootEtwTraceService.</summary>
public sealed class BootEtwTraceState
{
    public bool IsArmed { get; set; }
    public DateTime? ArmedAtUtc { get; set; }
    public string? OutputPath { get; set; }
}
