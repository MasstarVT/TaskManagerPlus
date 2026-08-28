namespace TaskManagerPlus.Models;

/// <summary>
/// One low-frequency (#625/#638: about once a minute, not per-tick) sample of CPU package
/// temperature and power draw, persisted to power-history-log.json (PowerHistoryLogService). This
/// is deliberately a different, much coarser log than the in-memory 60-sample CpuTempHistory/
/// PowerHistory charts EnergyThermalsViewModel already keeps - those don't survive an app restart
/// (and a Kernel-Power 41 unexpected-reboot event, by definition, always happens across a restart
/// boundary), so a small persisted trail is the only way to answer "what was this machine drawing
/// right before it rebooted" or "what were conditions when this WHEA error was logged" after the
/// fact. Kept intentionally coarse (about a day or two of history at minute resolution) - this is
/// a correlation aid for isolated crash/error timestamps, not a full telemetry log.
/// </summary>
public sealed class PowerTempSample
{
    public DateTime Timestamp { get; init; }

    /// <summary>Null when no CPU package temperature sensor was available at sample time.</summary>
    public double? TempC { get; init; }

    /// <summary>Null when no CPU package wattage sensor was available at sample time.</summary>
    public double? PackagePowerW { get; init; }

    /// <summary>Null when no discrete GPU (or no GPU wattage sensor) was available at sample time.</summary>
    public double? GpuPowerW { get; init; }
}
