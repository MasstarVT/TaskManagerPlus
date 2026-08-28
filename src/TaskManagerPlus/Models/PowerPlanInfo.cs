namespace TaskManagerPlus.Models;

/// <summary>Round 12, #90: one entry from `powercfg /list` - the built-in Windows power schemes
/// (Balanced/High performance/Power saver, plus any OEM- or user-added scheme).</summary>
public sealed class PowerPlanInfo
{
    public string Guid { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}

/// <summary>Round 12, #92: best-effort per-USB-device selective-suspend read - see
/// UsbPowerService's remarks for why <see cref="SelectiveSuspendEnabled"/> is nullable
/// ("Unknown") far more often than a definitive true/false on real hardware.</summary>
public sealed class UsbDevicePowerInfo
{
    public string Name { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public bool? SelectiveSuspendEnabled { get; init; }
}
