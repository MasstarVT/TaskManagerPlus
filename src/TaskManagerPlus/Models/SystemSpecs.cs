namespace TaskManagerPlus.Models;

/// <summary>
/// Static hardware/software inventory for the System tab. Unlike <see cref="HardwareSnapshot"/>
/// this is queried once (WMI lookups are relatively slow) rather than sampled every second.
/// </summary>
public sealed class SystemSpecs
{
    // Operating system
    public string OsName { get; init; } = string.Empty;
    public string OsVersion { get; init; } = string.Empty;
    public string OsArchitecture { get; init; } = string.Empty;
    public string OsInstallDate { get; init; } = string.Empty;

    // System / chassis
    public string ComputerName { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string SystemType { get; init; } = string.Empty;

    // Motherboard / BIOS
    public string MotherboardManufacturer { get; init; } = string.Empty;
    public string MotherboardProduct { get; init; } = string.Empty;
    public string BiosVersion { get; init; } = string.Empty;

    // CPU
    public string CpuName { get; init; } = string.Empty;
    public int CpuPhysicalCores { get; init; }
    public int CpuLogicalProcessors { get; init; }
    public double CpuMaxClockGhz { get; init; }

    // Memory
    public long RamTotalBytes { get; init; }
    public IReadOnlyList<MemoryModuleInfo> MemoryModules { get; init; } = Array.Empty<MemoryModuleInfo>();

    // Graphics
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = Array.Empty<GpuInfo>();

    // Storage
    public IReadOnlyList<DiskInfo> Disks { get; init; } = Array.Empty<DiskInfo>();
    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = Array.Empty<VolumeInfo>();
}

/// <summary>A single installed RAM stick, as reported by Win32_PhysicalMemory.</summary>
public sealed class MemoryModuleInfo
{
    public string Location { get; init; } = string.Empty;
    public long CapacityBytes { get; init; }
    public double SpeedMhz { get; init; }
    public string Manufacturer { get; init; } = string.Empty;
    public string MemoryType { get; init; } = string.Empty;
}

/// <summary>A single video adapter, as reported by Win32_VideoController.</summary>
public sealed class GpuInfo
{
    public string Name { get; init; } = string.Empty;
    public long AdapterRamBytes { get; init; }
    public string DriverVersion { get; init; } = string.Empty;
}

/// <summary>A single physical disk, as reported by Win32_DiskDrive.</summary>
public sealed class DiskInfo
{
    public string Model { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public string MediaType { get; init; } = string.Empty;
    public string InterfaceType { get; init; } = string.Empty;

    /// <summary>"OK", "Failure predicted", or the raw Win32_DiskDrive.Status string - see
    /// SystemSpecsService.ReadDisks for how this is derived.</summary>
    public string HealthStatus { get; init; } = "Unknown";

    /// <summary>True when the drive's SMART failure-prediction flag is set, or its WMI status
    /// is anything other than "OK" - drives this app's UI as a warning color.</summary>
    public bool IsHealthWarning { get; init; }
}

/// <summary>A single mounted volume (drive letter), for the free-space warning list.</summary>
public sealed class VolumeInfo
{
    public string Name { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public double PercentUsed => TotalBytes <= 0 ? 0 : (double)(TotalBytes - FreeBytes) / TotalBytes * 100.0;
}
