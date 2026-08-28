namespace TaskManagerPlus.Models;

/// <summary>#676: one GPU block's throttle-reason/ECC/retired-page readout from
/// `nvidia-smi -q -d PERFORMANCE,ECC,POWER` - see NvidiaSmiService for exactly how this is shelled
/// out to and parsed. The only *authoritative* GPU throttle-reason and VRAM-error source this app
/// can reach without a vendor SDK (LibreHardwareMonitorLib doesn't expose either), at the cost of
/// only being available on NVIDIA hardware with nvidia-smi.exe present. Every count field is
/// nullable and null (not 0) whenever nvidia-smi itself reports "N/A" - the common case on a
/// consumer card with no ECC memory, which must not be shown as "0 errors" (that would claim a
/// verified-clean ECC status this app never actually checked).</summary>
public sealed class NvidiaSmiGpuStatus
{
    public string GpuName { get; init; } = string.Empty;

    // ---- Clocks (Event) Throttle Reasons - explicit, driver-reported flags, not inferred. ----
    public bool SwPowerCap { get; init; }
    public bool HwThermalSlowdown { get; init; }
    public bool HwPowerBrake { get; init; }
    public bool SyncBoost { get; init; }

    public bool AnyThrottleActive => SwPowerCap || HwThermalSlowdown || HwPowerBrake || SyncBoost;

    // ---- ECC (null = "N/A" from nvidia-smi, e.g. no ECC memory on this card - not "0 errors"). ----
    public long? EccVolatileCorrectable { get; init; }
    public long? EccVolatileUncorrectable { get; init; }
    public long? RetiredPagesSingleBit { get; init; }
    public long? RetiredPagesDoubleBit { get; init; }
    public long? RemappedRows { get; init; }

    public bool HasEccData => EccVolatileCorrectable.HasValue || EccVolatileUncorrectable.HasValue;
}
