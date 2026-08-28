namespace TaskManagerPlus.Models;

/// <summary>
/// #478: one device's wake/power-management posture, combining `powercfg /devicequery`
/// (wake_armed/wake_from_any/wake_programmable - see PowerWakeQueryService) with a best-effort
/// per-device "Allow the computer to turn off this device to save power" read (see
/// DevicePowerCapabilityService). powercfg reports device *names* only, with no device instance ID
/// at all - a real limitation of the tool, not a parsing gap here - so MatchedDeviceId (and
/// therefore AllowComputerToTurnOff) is only ever populated when that name happened to match
/// exactly one currently-present device.
///
/// "Quick flag, not a verdict" per CLAUDE.md applies to AllowComputerToTurnOff specifically: it's
/// read from an undocumented PNPCapabilities registry convention, not a published cfgmgr32
/// contract - the same caveat this app already carries for its AV/mitigation-bitmask reads
/// elsewhere.
/// </summary>
public sealed class WakeDeviceInfo
{
    public string DeviceName { get; init; } = string.Empty;

    public string? MatchedDeviceId { get; init; }

    /// <summary>Currently armed to wake the system right now (powercfg /devicequery wake_armed).</summary>
    public bool IsWakeArmed { get; init; }

    /// <summary>Capable of waking the system from any sleep state (wake_from_any).</summary>
    public bool CanWakeFromAny { get; init; }

    /// <summary>Capable of a programmable/conditional wake, e.g. a scheduled-task wake
    /// (wake_programmable).</summary>
    public bool CanWakeProgrammable { get; init; }

    public bool? AllowComputerToTurnOff { get; init; }
    public string AllowComputerToTurnOffText => AllowComputerToTurnOff switch
    {
        true => "Yes",
        false => "No",
        _ => "Unknown",
    };
}
