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

    /// <summary>Days since Windows install, computed alongside OsInstallDate (which only keeps
    /// the display string) - null when the install date couldn't be read.</summary>
    public int? OsInstallAgeDays { get; init; }

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

    /// <summary>Total physical RAM slots on the motherboard (Win32_PhysicalMemoryArray.MemoryDevices,
    /// #16) - null when that class isn't available. Compared against MemoryModules.Count to show
    /// "N of M slots populated".</summary>
    public int? TotalMemorySlots { get; init; }

    // Graphics
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = Array.Empty<GpuInfo>();

    // Storage
    public IReadOnlyList<DiskInfo> Disks { get; init; } = Array.Empty<DiskInfo>();
    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = Array.Empty<VolumeInfo>();

    // Security posture (TPM / Secure Boot / VBS)
    public SecurityInfo Security { get; init; } = new();

    // Third-party drivers old enough to be worth a "check for an update" nudge.
    public IReadOnlyList<DriverInfo> OutdatedDrivers { get; init; } = Array.Empty<DriverInfo>();

    // Recently installed Windows updates/hotfixes (#57) - correlates with "when did the problem start".
    public IReadOnlyList<UpdateInfo> RecentUpdates { get; init; } = Array.Empty<UpdateInfo>();

    // Registered antivirus/security products (#63).
    public IReadOnlyList<AntivirusInfo> AntivirusProducts { get; init; } = Array.Empty<AntivirusInfo>();

    /// <summary>True when more than one AV product looks actively enabled - conflicting
    /// real-time scanners are a classic, often-invisible perf killer.</summary>
    public bool MultipleActiveAvWarning { get; init; }

    /// <summary>Recently installed third-party software (#68), from the Uninstall registry keys'
    /// InstallDate values - correlates with "when did the problem start". Windows keeps no log of
    /// *uninstalled* software, so this is deliberately install-only rather than a full timeline.</summary>
    public IReadOnlyList<InstalledSoftwareInfo> RecentlyInstalledSoftware { get; init; } = Array.Empty<InstalledSoftwareInfo>();

    /// <summary>USB devices currently enumerated by Windows (#69), flagged when Windows reports a
    /// non-OK status/error code - helps catch a misbehaving peripheral or a failing USB
    /// controller/hub. Per-device power draw isn't included: there's no public, reliable Windows
    /// API for it (the same reason Round 3's per-process power figure was left out).</summary>
    public IReadOnlyList<UsbDeviceInfo> UsbDevices { get; init; } = Array.Empty<UsbDeviceInfo>();

    /// <summary>Where the page file lives and whether that's the boot/system drive (#70) - a page
    /// file left on a slower secondary HDD (or vice versa, an SSD's page file effectively wasted
    /// on a system that boots from HDD) is a common, silent slowdown cause on multi-drive systems.</summary>
    public PageFileLocationInfo? PageFileLocation { get; init; }
}

/// <summary>One entry from the Uninstall registry keys with a parsed install date (#68).</summary>
public sealed class InstalledSoftwareInfo
{
    public string Name { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public DateTime InstallDate { get; init; }
}

/// <summary>One USB device as reported by Win32_PnPEntity (#69).</summary>
public sealed class UsbDeviceInfo
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;

    /// <summary>Win32_PnPEntity.ConfigManagerErrorCode - 0 means "no problem reported" (Device
    /// Manager's own "This device is working properly" state); anything else is a real,
    /// Windows-reported enumeration/driver problem.</summary>
    public int ConfigManagerErrorCode { get; init; }
    public bool HasError => ConfigManagerErrorCode != 0;
}

/// <summary>Page file drive letter and the media type of the physical disk backing it (#70).</summary>
public sealed class PageFileLocationInfo
{
    public string DriveLetter { get; init; } = string.Empty;
    public string MediaType { get; init; } = "Unknown";
    public bool IsSameAsBootDrive { get; init; }
}

/// <summary>One installed Windows update/hotfix, as reported by Win32_QuickFixEngineering.</summary>
public sealed class UpdateInfo
{
    public string HotFixId { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTime? InstalledOn { get; init; }
}

/// <summary>One registered antivirus/security product, as reported by the SecurityCenter2 WMI
/// namespace. See SystemSpecsService.ReadAntivirusProducts for the "enabled" heuristic caveat.</summary>
public sealed class AntivirusInfo
{
    public string Name { get; init; } = string.Empty;
    public bool LooksEnabled { get; init; }
}

/// <summary>TPM / Secure Boot / VBS (Core Isolation) posture, for the System tab's security card.
/// Every field is nullable/"Unknown" by design - each comes from a WMI class or registry key that
/// can legitimately be unavailable (older hardware, a locked-down environment, or - for TPM
/// specifically - a permission that even this app's elevation doesn't always satisfy), and
/// "Unknown" needs to render distinctly from "confirmed off".</summary>
public sealed class SecurityInfo
{
    public bool? SecureBootEnabled { get; init; }
    public bool? TpmPresent { get; init; }
    public bool? TpmReady { get; init; }
    public string TpmVersion { get; init; } = string.Empty;
    public bool? VbsRunning { get; init; }
    public IReadOnlyList<string> VbsServicesRunning { get; init; } = Array.Empty<string>();
}

/// <summary>One third-party driver flagged as old enough to be worth checking for an update -
/// see SystemSpecsService.ReadOutdatedDrivers for the filtering rules and why they exist.</summary>
public sealed class DriverInfo
{
    public string DeviceName { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string DriverVersion { get; init; } = string.Empty;
    public DateTime? DriverDate { get; init; }
}

/// <summary>A single installed RAM stick, as reported by Win32_PhysicalMemory.</summary>
public sealed class MemoryModuleInfo
{
    public string Location { get; init; } = string.Empty;
    public long CapacityBytes { get; init; }

    /// <summary>The module's rated speed (Win32_PhysicalMemory.Speed, i.e. what's printed on the
    /// SPD label - e.g. "3200" for a DDR4-3200 stick), #16.</summary>
    public double SpeedMhz { get; init; }

    /// <summary>The speed Windows actually detected it running at (ConfiguredClockSpeed, #16) -
    /// lower than SpeedMhz means XMP/DOCP (or the motherboard's equivalent) isn't enabled, a
    /// common and otherwise invisible "why is my PC slower than it should be" cause.</summary>
    public double ConfiguredSpeedMhz { get; init; }

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

    /// <summary>SSD wear/life-used percentage (#65), from the Storage Management API's
    /// MSFT_StorageReliabilityCounter.Wear (the same figure PowerShell's Get-StorageReliabilityCounter
    /// reports) - 0 = fresh, 100 = manufacturer-rated end of life. Null when unavailable: NVMe/SATA
    /// driver support for this varies, and matching a physical disk to its reliability counter is
    /// itself a best-effort index-based pairing (see SystemSpecsService.ReadDiskWearByIndex), so
    /// this degrades to "not shown" rather than a false reading, the same tier as the SMART
    /// failure-prediction flag above.</summary>
    public int? WearPercent { get; init; }
}

/// <summary>A single mounted volume (drive letter), for the free-space warning list.</summary>
public sealed class VolumeInfo
{
    public string Name { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public double PercentUsed => TotalBytes <= 0 ? 0 : (double)(TotalBytes - FreeBytes) / TotalBytes * 100.0;

    /// <summary>File system dirty bit (#29, via FSCTL_IS_VOLUME_DIRTY) - true means the volume
    /// needs a chkdsk pass (e.g. after an unclean shutdown). Null when the check itself couldn't
    /// run (needs a handle to the raw volume - can fail even elevated on some configurations) -
    /// "Unknown", not "clean".</summary>
    public bool? IsDirty { get; init; }
}
