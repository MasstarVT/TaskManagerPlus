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

    /// <summary>#92: BIOS/UEFI release date age, in days. Windows has no generic "is a newer BIOS
    /// available" API (that's vendor-specific, e.g. Dell Command | Update / Lenovo Vantage), so
    /// this is a proxy hint rather than a real "update available" flag - an old release date is
    /// worth a manual check on the motherboard/OEM support page, framed exactly that way in the UI
    /// rather than claiming to know an update actually exists.</summary>
    public int? BiosAgeDays { get; init; }

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

    /// <summary>#441: CAS latency / primary timings, read via LibreHardwareMonitorLib's Memory
    /// hardware-type sensors where the chipset's SPD reader is supported - see
    /// SystemSpecsService.ReadMemoryTimingsText. LibreHardwareMonitorLib 0.9.6 does not expose SPD
    /// timing sensors on any chipset backend at the time this was written, so this reads
    /// "Unknown - ..." on every system tested; the lookup itself is real (by sensor name hint, the
    /// same pattern SensorMonitorService.FindByNameContains uses elsewhere), not a hardcoded
    /// string, so it starts reporting real figures automatically if a future LibreHardwareMonitorLib
    /// version adds SPD/timing sensors for a given board.</summary>
    public string MemoryTimingsText { get; init; } = "Unknown";

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

    // Round 10 additions (#57-64) - see each field's own remarks below.
    public string ChassisType { get; init; } = "Unknown";
    public string ActivationStatus { get; init; } = "Unknown";
    public IReadOnlyList<string> DotNetRuntimes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<MonitorInfo> Monitors { get; init; } = Array.Empty<MonitorInfo>();
    public string ChipsetDriverText { get; init; } = "Unknown";

    /// <summary>Defender exclusion list (#63) - null means the registry key itself couldn't be read
    /// (Tamper Protection or policy can deny this even elevated), an empty (non-null) list means it
    /// was read and genuinely has nothing configured. Kept distinct so the UI can show "Unknown/
    /// inaccessible" rather than a false "no exclusions".</summary>
    public IReadOnlyList<string>? DefenderExclusions { get; init; }

    public string SystemUuid { get; init; } = string.Empty;
    public string CpuIdentifier { get; init; } = string.Empty;

    /// <summary>Round 11, #73: Windows Update/servicing reboot-pending flag - see
    /// SystemSpecsService.ReadRebootPending for which indicator keys are checked.</summary>
    public bool RebootPending { get; init; }

    /// <summary>#445: the memory array's maximum supported capacity (Win32_PhysicalMemoryArray.
    /// MaxCapacity/ExtendedMaxCapacity) - null when that class/field isn't available, same tier as
    /// TotalMemorySlots above.</summary>
    public long? MemoryArrayMaxCapacityBytes { get; init; }

    /// <summary>#446: Win32_PhysicalMemoryArray.MemoryErrorCorrection, as plain text ("None",
    /// "Single-bit ECC", ...) - "Unknown" when the class/field isn't available or reports the
    /// documented "Unknown" code itself.</summary>
    public string MemoryArrayErrorCorrectionText { get; init; } = "Unknown";

    /// <summary>#446: the combined ECC verdict text (array-level field + per-module SMBIOS width
    /// evidence) - see MemoryDiagnosticsService.DescribeEcc. What the System Specs card actually
    /// displays; MemoryArrayErrorCorrectionText above is kept as the raw WMI value.</summary>
    public string MemoryEccStatusText { get; init; } = "Unknown";

    /// <summary>#444: channel-population summary text and whether every populated module landed on
    /// the same channel - see MemoryDiagnosticsService.CheckChannelPopulation.</summary>
    public string MemoryChannelText { get; init; } = string.Empty;
    public bool MemoryChannelWarning { get; init; }

    /// <summary>#447: corrected-memory-error events (Microsoft-Windows-WHEA-Logger, event ID 47)
    /// found within EventLogService's lookback window - see EventLogService.ReadCorrectedMemoryErrors.</summary>
    public int CorrectedMemoryErrorCount { get; init; }
    public DateTime? LastCorrectedMemoryError { get; init; }
    public IReadOnlyList<CorrectedMemoryErrorEvent> CorrectedMemoryErrors { get; init; } = Array.Empty<CorrectedMemoryErrorEvent>();

    /// <summary>#449: the most recent Windows Memory Diagnostic (mdsched.exe) result, null when
    /// none was ever found in the retained System log ("never run", not "passed").</summary>
    public MemoryDiagnosticResultInfo? MemoryDiagnosticResult { get; init; }

    /// <summary>#451: single RAM health rollup - see RamHealthSummary's remarks.</summary>
    public RamHealthSummary RamHealth { get; init; } = new();
}

/// <summary>One active display (#60) - resolution/refresh rate from Win32_VideoController's current
/// mode fields, connection type (best-effort) from the root\wmi WmiMonitorConnectionParams class -
/// see SystemSpecsService.ReadMonitors for exactly how (and when) these two sources are paired.
/// HDR support has no reliable enumeration source short of DXGI/IDXGIOutput6 COM interop (a
/// materially higher risk tier than anything else this app takes on), so it's deliberately left out
/// rather than guessed.</summary>
public sealed class MonitorInfo
{
    public string Name { get; init; } = "Display";
    public int WidthPx { get; init; }
    public int HeightPx { get; init; }
    public int RefreshHz { get; init; }
    public string ConnectionType { get; init; } = "Unknown";
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

    // #440: SMBIOS Type 17 extras WMI drops entirely - merged in by SystemSpecsService.ReadMemoryModules
    // via SmbiosMemoryService, matched to this WMI-sourced row by DeviceLocator. Every field below
    // stays at its default ("Unknown"/null) when no matching SMBIOS structure was found (raw table
    // unreadable, or a DeviceLocator that didn't match anything WMI reported) - never a guess.
    // Settable (not init) for the same reason IsMismatched/ChannelLabel below are: this WMI row is
    // constructed first, then SystemSpecsService.ApplySmbiosData fills these in as a second pass
    // once the matching SMBIOS structure (if any) has been found.
    public string PartNumber { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string BankLocator { get; set; } = string.Empty;
    public string FormFactor { get; set; } = "Unknown";
    public string MemoryTechnology { get; set; } = "Unknown";
    public int? RankCount { get; set; }
    public double? MinVoltageV { get; set; }
    public double? MaxVoltageV { get; set; }
    public double? ConfiguredVoltageV { get; set; }

    /// <summary>Total data-transfer width including ECC/check bits, vs. DataWidthBits below - #446's
    /// per-module ECC evidence (TotalWidthBits &gt; DataWidthBits, e.g. 72 vs. 64, means this specific
    /// module carries check bits). Both null when SMBIOS data wasn't available for this module.</summary>
    public int? TotalWidthBits { get; set; }
    public int? DataWidthBits { get; set; }

    /// <summary>True when this module's own width fields indicate ECC, false when they indicate no
    /// ECC, null when the width fields aren't available at all (no SMBIOS match) - kept distinct
    /// from a hardcoded false so "Unknown" and "confirmed non-ECC" never render the same way.</summary>
    public bool? HasEccWidth => TotalWidthBits.HasValue && DataWidthBits.HasValue
        ? TotalWidthBits.Value > DataWidthBits.Value
        : null;

    /// <summary>#443: "quick flag, not a verdict" - true when this module differs from the other
    /// populated slots in a way SystemSpecsService.ReadMemoryModules flagged (part number, capacity,
    /// rated speed, rank, or manufacturer). Settable (not init) rather than the rest of this class'
    /// init-only fields: SystemSpecsService.ReadMemoryModules fills the rest of each row in first,
    /// then a second pass (MemoryDiagnosticsService.DetectMismatches) assigns this and the two
    /// fields below once every module in the array is known - a mismatch is inherently a
    /// cross-module comparison, not a fact any single WMI/SMBIOS row carries on its own.</summary>
    public bool IsMismatched { get; set; }
    public string MismatchReason { get; set; } = string.Empty;

    /// <summary>#444: best-effort channel label parsed from DeviceLocator/BankLocator (e.g. "A", "B") -
    /// empty when no recognizable channel letter could be parsed, in which case this module is
    /// excluded from the channel-population check rather than guessed into a bucket.</summary>
    public string ChannelLabel { get; set; } = string.Empty;
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

    /// <summary>Win32_DiskDrive.Index (#38/round 9) - the same small integer
    /// MSFT_StorageReliabilityCounter.DeviceId is paired against for WearPercent above. Kept here
    /// so the Storage tab's on-demand full-SMART-table lookup can address a disk without a second
    /// WMI round trip to re-discover it.</summary>
    public int Index { get; init; } = -1;
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

    /// <summary>"HDD"/"SSD"/"SCM"/"Unknown" (round 9, #44) - reused from DiskFragmentationService's
    /// media-type associator chain to decide which volumes TRIM status is even meaningful for.</summary>
    public string MediaType { get; init; } = "Unknown";

    /// <summary>BitLocker conversion/protection status (round 9, #37), from Win32_EncryptableVolume
    /// in root\CIMV2\Security\MicrosoftVolumeEncryption - see VolumeDiagnosticsService.ReadBitLockerStatus
    /// for why this degrades to "Unknown" rather than a false "Off" on several legitimate failure
    /// paths (namespace/method access can be denied even elevated on some Windows SKUs/policies,
    /// the same honesty tradeoff the TPM/VBS reads in this file already take).</summary>
    public string BitLockerStatus { get; init; } = "Unknown";

    /// <summary>Recycle Bin size on this volume (round 9, #40), via the native SHQueryRecycleBinW
    /// call - null on any failure (e.g. no Recycle Bin folder on a removable/network volume).</summary>
    public long? RecycleBinBytes { get; init; }

    /// <summary>Shadow copy (VSS) storage currently used on this volume (round 9, #42), via
    /// `vssadmin list shadowstorage` - null when VSS isn't configured for this volume at all (the
    /// common case unless System Restore/File History is enabled), not an error.</summary>
    public long? ShadowCopyBytes { get; init; }

    /// <summary>TRIM (delete notify) status (round 9, #44), via `fsutil behavior query
    /// DisableDeleteNotify` - true means TRIM is enabled. Only meaningful for SSD volumes; null on
    /// an HDD volume (mirrors how HDD fragmentation is hidden for SSDs) or when the check itself
    /// couldn't run.</summary>
    public bool? TrimEnabled { get; init; }
}
