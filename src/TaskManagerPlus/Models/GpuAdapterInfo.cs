namespace TaskManagerPlus.Models;

/// <summary>One GPU engine type's utilization percent for one adapter (#53), aggregated across
/// every process using it - the same "GPU Engine" perf-counter category Round 4's per-process GPU
/// column reads (ProcessMonitorService.ReadGpuUsageByPid), just summed per-adapter here instead of
/// per-process. Engine type names ("3D", "Copy", "Video Decode", "Video Encode", ...) come straight
/// from the instance name's "engtype_X" suffix, split from PascalCase for display.</summary>
public sealed class GpuEngineUsage
{
    public string EngineType { get; init; } = string.Empty;
    public double Percent { get; init; }
}

/// <summary>One GPU adapter's live utilization/VRAM snapshot (#53/#54), for the GPU tab's engine
/// utilization card. Identity fields (Name/DriverVersion/DriverDate/WddmVersion/DedicatedVramTotalBytes)
/// are only populated when this live LUID group could be paired with a Win32_VideoController entry
/// - see GpuMonitorService.Sample for exactly when that pairing is (and isn't) attempted, and why a
/// mismatch degrades to a generic "GPU N" label rather than guessing which adapter a LUID belongs
/// to.</summary>
public sealed class GpuAdapterSnapshot
{
    public string Luid { get; init; } = string.Empty;
    public string Name { get; init; } = "GPU";
    public bool NameIsExact { get; init; }
    public bool IsIntegrated { get; init; }
    public List<GpuEngineUsage> Engines { get; init; } = new();

    /// <summary>The highest single engine's utilization - the same "one overall number" Task
    /// Manager's own GPU column shows, since a GPU doing 80% 3D work and 5% Copy work at once is
    /// reasonably described as "80% busy", not summed past 100.</summary>
    public double TotalUtilizationPercent { get; init; }

    public long DedicatedVramUsedBytes { get; init; }
    public long DedicatedVramTotalBytes { get; init; }
    public long SharedVramUsedBytes { get; init; }

    public double DedicatedVramPercent => DedicatedVramTotalBytes > 0
        ? Math.Clamp((double)DedicatedVramUsedBytes / DedicatedVramTotalBytes * 100.0, 0, 100)
        : 0;

    public string DriverVersion { get; init; } = string.Empty;
    public string DriverDate { get; init; } = string.Empty;
    public string WddmVersion { get; init; } = "Unknown";
}

/// <summary>One installed GPU adapter's static identity (#55/#56), from Win32_VideoController plus
/// a registry read for the corrected 64-bit VRAM figure and a best-effort WDDM version - always
/// shown in full on the GPU tab's "Installed adapters" card regardless of whether live utilization
/// data could be paired with it (see GpuAdapterSnapshot's remarks), so a secondary/idle GPU nothing
/// is actively using still shows up in the multi-GPU list.</summary>
public sealed class GpuAdapterIdentity
{
    public string Name { get; init; } = "Unknown GPU";
    public bool IsIntegrated { get; init; }
    public long AdapterRamBytes { get; init; }
    public string DriverVersion { get; init; } = string.Empty;
    public string DriverDate { get; init; } = string.Empty;

    /// <summary>Best-effort major.minor WDDM version, from the driver's own "WddmVersion" REG_DWORD
    /// under its Class subkey - not a documented Microsoft contract (see
    /// GpuMonitorService.ReadRegistryAdapterInfo), so "Unknown" when that value isn't present rather
    /// than a guessed number.</summary>
    public string WddmVersion { get; init; } = "Unknown";
}
