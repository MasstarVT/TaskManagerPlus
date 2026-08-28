namespace TaskManagerPlus.Models;

/// <summary>#670: one GPU driver timeout/reset (TDR) event, parsed from the System log's "Display"
/// provider events 4101/4104 - the same raw event id EventLogService.TdrEventId already counts for
/// the Stability tab's TdrEventCount, just parsed here for the *detail* the flat count doesn't
/// carry (which driver module, and whether it actually recovered). See
/// EventLogService.ReadGpuTdrEvents for exactly how DriverModule/Recovered are extracted - both are
/// best-effort text parses of an undocumented, unversioned message layout (same tier as
/// EventLogService.ExtractBugcheckCode), so "Unknown"/null is the expected outcome on an OS build
/// whose wording doesn't match.</summary>
public sealed class GpuTdrEvent
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }

    /// <summary>The failing driver module (nvlddmkm, amdkmdag, igdkmd64, atikmdag, ...) parsed out
    /// of the event's own formatted message - "Unknown" when the message doesn't name one.</summary>
    public string DriverModule { get; init; } = "Unknown";

    /// <summary>True when the message says the driver "successfully recovered" (the common,
    /// non-fatal case - the desktop kept running); false when the message describes a reset without
    /// that phrase (the driver was reset but recovery wasn't confirmed in text); null when the
    /// message doesn't clearly say either way.</summary>
    public bool? Recovered { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>#677: an Application-log record naming DXGI_ERROR_DEVICE_REMOVED (HRESULT 0x887A0005) -
/// the class of crash a game/renderer logs when the OS tore its GPU device down out from under it,
/// distinct from (but often caused by) the TDR events above. Best-effort provider/process name only
/// - this app doesn't parse the .NET/native crash-dump payload itself, just the event's own
/// formatted text, the same tier as StabilityEvent.FaultingModule.</summary>
public sealed class GpuDeviceRemovedEvent
{
    public DateTime TimeCreated { get; init; }
    public string ProviderName { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>#677: one detected "engine flatline while a foreground app was running" - the GPU tab's
/// own live per-tick pre-TDR hang detector (GpuViewModel.DetectEngineFlatline), not an event-log
/// read. Persisted via GpuHangHistoryService the same way PowerPlanHistoryService persists power-
/// plan changes, so the Stability tab can show past sessions' hangs too, not just this session's.</summary>
public sealed class GpuHangEvent
{
    public DateTime DetectedAt { get; init; }
    public string AdapterName { get; init; } = string.Empty;

    /// <summary>The "3D" engine utilization percent that stopped changing.</summary>
    public double StuckAtPercent { get; init; }
    public double DurationSeconds { get; init; }
    public string ForegroundProcessName { get; init; } = string.Empty;
}

/// <summary>#677: "unrecovered" reset counting - a TDR or device-removed event is counted as
/// unrecovered when a Kernel-Power 41 bugcheck naming 0x116 (VIDEO_TDR_ERROR) or 0x117
/// (VIDEO_TDR_TIMEOUT_DETECTED) landed within a few minutes of it, the same "nearest event within a
/// short window" correlation EventLogService.ReadMinidumps already uses for minidump-to-bugcheck
/// pairing. A TDR that *didn't* end in one of those bugchecks recovered on its own (the common
/// case); one that did took the whole system down with it.</summary>
public sealed class GpuResetSummary
{
    public List<GpuTdrEvent> TdrEvents { get; init; } = new();
    public List<GpuDeviceRemovedEvent> DeviceRemovedEvents { get; init; } = new();
    public int UnrecoveredResetCount { get; init; }
}
