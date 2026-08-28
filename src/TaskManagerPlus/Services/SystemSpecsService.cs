using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Reads the static hardware/software inventory shown on the System tab: OS, CPU, RAM sticks,
/// GPUs, motherboard/BIOS and disks. Everything comes from WMI (Win32_* classes) except GPU VRAM,
/// which WMI reports through a 32-bit field that misreports (or caps at ~4 GB) on modern cards, so
/// that one field is read from the driver's registry key instead, the same place Device Manager
/// and most diagnostic tools get it from.
/// </summary>
public sealed class SystemSpecsService
{
    private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    public async Task<SystemSpecs> QueryAsync()
    {
        var (osName, osVersion, osArch, osInstallDate, osInstallAgeDays) = ReadOperatingSystem();
        var (manufacturer, model, systemType) = ReadComputerSystem();
        var (boardManufacturer, boardProduct) = ReadBaseBoard();
        var (biosVersion, biosAgeDays) = ReadBios();
        var (cpuName, physicalCores, logicalProcessors, maxClockGhz) = ReadCpu();
        var memoryModules = ReadMemoryModules();
        long ramTotal = memoryModules.Sum(m => m.CapacityBytes);
        var totalMemorySlots = ReadTotalMemorySlots();
        var hwIds = ReadHardwareIdentifiers();

        // #732: bcdedit's hypervisorlaunchtype is read once here (via the same BcdInspectorService
        // #724-731 use on the Startup tab) rather than re-implemented locally - it's just one more
        // value off the current loader entry the parser already exposes.
        var bcdStore = await BcdInspectorService.ReadAsync();
        string? hypervisorLaunchType = bcdStore.CurrentEntry?.Get("hypervisorlaunchtype");

        return new SystemSpecs
        {
            OsName = osName,
            OsVersion = osVersion,
            OsArchitecture = osArch,
            OsInstallDate = osInstallDate,
            OsInstallAgeDays = osInstallAgeDays,

            ComputerName = Environment.MachineName,
            Manufacturer = manufacturer,
            Model = model,
            SystemType = systemType,

            MotherboardManufacturer = boardManufacturer,
            MotherboardProduct = boardProduct,
            BiosVersion = biosVersion,
            BiosAgeDays = biosAgeDays,

            CpuName = cpuName,
            CpuPhysicalCores = physicalCores,
            CpuLogicalProcessors = logicalProcessors,
            CpuMaxClockGhz = maxClockGhz,

            RamTotalBytes = ramTotal,
            MemoryModules = memoryModules,
            TotalMemorySlots = totalMemorySlots,

            Gpus = ReadGpus(),
            Disks = ReadDisks(),
            Volumes = await ReadVolumesAsync(),

            Security = ReadSecurityInfo(hypervisorLaunchType),
            OutdatedDrivers = ReadOutdatedDrivers(),
            RecentUpdates = ReadRecentHotfixes(),
            AntivirusProducts = ReadAntivirusProducts(out var multipleActive),
            MultipleActiveAvWarning = multipleActive,
            RecentlyInstalledSoftware = ReadRecentlyInstalledSoftware(),
            UsbDevices = ReadUsbDevices(),
            PageFileLocation = ReadPageFileLocation(),

            ChassisType = ReadChassisType(),
            ActivationStatus = ReadActivationStatus(),
            DotNetRuntimes = ReadDotNetRuntimes(),
            Monitors = ReadMonitors(),
            ChipsetDriverText = ReadChipsetDriverInfo(),
            DefenderExclusions = ReadDefenderExclusions(),
            SystemUuid = hwIds.Uuid,
            CpuIdentifier = hwIds.CpuId,

            RebootPending = ReadRebootPending(),

            // #733: firmware boot mode + system disk partition style - see ReadFirmwareDiskInfo.
            FirmwareDisk = ReadFirmwareDiskInfo(),
        };
    }

    /// <summary>Round 11, #73: Windows Update/servicing reboot-pending check - reads a few
    /// well-known, widely-documented indicator keys rather than an exhaustive enumeration of
    /// every possible reboot-pending source (there are many, undocumented and version-specific):
    /// the Component Based Servicing key (set after most cumulative-update installs), the Windows
    /// Update Auto Update client's own key, and PendingFileRenameOperations (set by any installer
    /// - not just Windows Update - that couldn't replace a file still in use). Any one present
    /// means true; a denied/failed read degrades to false ("not pending") rather than risk a false
    /// positive, the same "don't over-claim" tradeoff the volume dirty-bit check already takes.</summary>
    private static bool ReadRebootPending()
    {
        try
        {
            using var cbs = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbs is not null) return true;
        }
        catch { /* ignore - fall through to the next indicator */ }

        try
        {
            using var wu = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wu is not null) return true;
        }
        catch { /* ignore */ }

        try
        {
            using var session = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (session?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 }) return true;
        }
        catch { /* ignore */ }

        return false;
    }

    private static (string Name, string Version, string Architecture, string InstallDate, int? InstallAgeDays) ReadOperatingSystem()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Caption, Version, OSArchitecture, InstallDate FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = (mo["Caption"] as string ?? "Unknown OS").Trim();
                string version = (mo["Version"] as string ?? string.Empty).Trim();
                string arch = (mo["OSArchitecture"] as string ?? string.Empty).Trim();
                string installDate = string.Empty;
                int? installAgeDays = null;
                if (mo["InstallDate"] is string wmiDate)
                {
                    try
                    {
                        var parsed = ManagementDateTimeConverter.ToDateTime(wmiDate);
                        installDate = parsed.ToShortDateString();
                        installAgeDays = Math.Max(0, (int)(DateTime.Now - parsed).TotalDays);
                    }
                    catch { /* leave blank */ }
                }
                return (name, version, arch, installDate, installAgeDays);
            }
        }
        catch
        {
            // fall through to defaults
        }
        return ("Unknown OS", string.Empty, string.Empty, string.Empty, null);
    }

    private static (string Manufacturer, string Model, string SystemType) ReadComputerSystem()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Model, SystemType FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                return (
                    (mo["Manufacturer"] as string ?? string.Empty).Trim(),
                    (mo["Model"] as string ?? string.Empty).Trim(),
                    (mo["SystemType"] as string ?? string.Empty).Trim());
            }
        }
        catch
        {
            // fall through to defaults
        }
        return (string.Empty, string.Empty, string.Empty);
    }

    private static (string Manufacturer, string Product) ReadBaseBoard()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject mo in searcher.Get())
            {
                return (
                    (mo["Manufacturer"] as string ?? string.Empty).Trim(),
                    (mo["Product"] as string ?? string.Empty).Trim());
            }
        }
        catch
        {
            // fall through to defaults
        }
        return (string.Empty, string.Empty);
    }

    /// <summary>
    /// #92: BIOS version plus its release-date age. Windows has no cross-vendor "update
    /// available" check for firmware - only OEM-specific tools (Dell Command | Update, Lenovo
    /// Vantage, ...) know that - so this reads Win32_BIOS.ReleaseDate as a "worth a manual check"
    /// proxy instead of a real update-available flag, the same honesty tradeoff
    /// ReadOutdatedDrivers' date-based filtering already takes for third-party drivers.
    /// </summary>
    private static (string Version, int? AgeDays) ReadBios()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            foreach (ManagementObject mo in searcher.Get())
            {
                string version = (mo["SMBIOSBIOSVersion"] as string ?? string.Empty).Trim();

                int? ageDays = null;
                if (mo["ReleaseDate"] is string wmiDate)
                {
                    try
                    {
                        var released = ManagementDateTimeConverter.ToDateTime(wmiDate);
                        ageDays = Math.Max(0, (int)(DateTime.Now - released).TotalDays);
                    }
                    catch { /* leave null */ }
                }
                return (version, ageDays);
            }
        }
        catch
        {
            // fall through to default
        }
        return (string.Empty, null);
    }

    private static (string Name, int PhysicalCores, int LogicalProcessors, double MaxClockGhz) ReadCpu()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, MaxClockSpeed, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = (mo["Name"] as string ?? "Unknown CPU").Trim();
                double maxClockMhz = System.Convert.ToDouble(mo["MaxClockSpeed"] ?? 0.0);
                int cores = System.Convert.ToInt32(mo["NumberOfCores"] ?? Environment.ProcessorCount);
                int logical = System.Convert.ToInt32(mo["NumberOfLogicalProcessors"] ?? Environment.ProcessorCount);
                return (name, cores, logical, Math.Round(maxClockMhz / 1000.0, 2));
            }
        }
        catch
        {
            // fall through to defaults
        }
        return ("Unknown CPU", 0, Environment.ProcessorCount, 0);
    }

    private static List<MemoryModuleInfo> ReadMemoryModules()
    {
        var modules = new List<MemoryModuleInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            foreach (ManagementObject mo in searcher.Get())
            {
                long capacity = 0;
                try { capacity = System.Convert.ToInt64(mo["Capacity"] ?? 0L); } catch { /* leave 0 */ }

                double speed = 0;
                try { speed = System.Convert.ToDouble(mo["Speed"] ?? 0.0); } catch { /* leave 0 */ }

                // #16: the speed Windows actually detected the module running at - lower than the
                // rated Speed above means XMP/DOCP isn't enabled.
                double configuredSpeed = 0;
                try { configuredSpeed = System.Convert.ToDouble(mo["ConfiguredClockSpeed"] ?? 0.0); } catch { /* leave 0 */ }

                int smBiosType = 0;
                try { smBiosType = System.Convert.ToInt32(mo["SMBIOSMemoryType"] ?? 0); } catch { /* leave 0 */ }

                modules.Add(new MemoryModuleInfo
                {
                    Location = (mo["DeviceLocator"] as string ?? "RAM").Trim(),
                    CapacityBytes = capacity,
                    SpeedMhz = speed,
                    ConfiguredSpeedMhz = configuredSpeed,
                    Manufacturer = (mo["Manufacturer"] as string ?? string.Empty).Trim(),
                    MemoryType = DdrGenerationName(smBiosType),
                });
            }
        }
        catch
        {
            // return whatever was gathered before the failure
        }
        return modules.OrderBy(m => m.Location, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Total physical RAM slots on the motherboard (#16), compared against the populated-
    /// module count above to show "N of M slots populated" - a quick, otherwise invisible signal
    /// that there's headroom to add more RAM without an upgrade.</summary>
    private static int? ReadTotalMemorySlots()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
            foreach (ManagementObject mo in searcher.Get())
                return System.Convert.ToInt32(mo["MemoryDevices"] ?? 0);
        }
        catch
        {
            // Class unavailable on this system - "N modules installed" alone, no slot count.
        }
        return null;
    }

    // SMBIOSMemoryType codes from the SMBIOS spec (DMTF), limited to the generations still in use.
    private static string DdrGenerationName(int smBiosType) => smBiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        34 => "DDR5",
        _ => "Unknown",
    };

    private static List<GpuInfo> ReadGpus()
    {
        var gpus = new List<GpuInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = (mo["Name"] as string ?? "Unknown GPU").Trim();

                long adapterRam = 0;
                try { adapterRam = System.Convert.ToInt64(mo["AdapterRAM"] ?? 0L); } catch { /* leave 0 */ }

                long vram = ReadVramFromRegistry(name) ?? adapterRam;

                gpus.Add(new GpuInfo
                {
                    Name = name,
                    AdapterRamBytes = vram,
                    DriverVersion = (mo["DriverVersion"] as string ?? string.Empty).Trim(),
                });
            }
        }
        catch
        {
            // return whatever was gathered before the failure
        }
        return gpus;
    }

    /// <summary>
    /// Win32_VideoController.AdapterRAM is a 32-bit field, so it wraps/misreports VRAM on cards
    /// with 4 GB or more. The driver writes the real value under its Class\{guid}\NNNN key as a
    /// 64-bit "HardwareInformation.qwMemorySize" - this walks those subkeys looking for the one
    /// whose DriverDesc matches the adapter name from WMI.
    /// </summary>
    private static long? ReadVramFromRegistry(string gpuName)
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Class\{DisplayClassGuid}");
            if (classKey is null) return null;

            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                if (!uint.TryParse(subKeyName, out _)) continue; // adapter subkeys are "0000", "0001", ... - skip "Properties" etc.

                using var sub = classKey.OpenSubKey(subKeyName);
                if (sub is null) continue;

                if (sub.GetValue("DriverDesc") is not string desc ||
                    !desc.Equals(gpuName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var raw = sub.GetValue("HardwareInformation.qwMemorySize");
                if (raw is long l) return l;
                if (raw is int i) return i;
            }
        }
        catch
        {
            // fall back to the WMI value
        }
        return null;
    }

    private static List<DiskInfo> ReadDisks()
    {
        var disks = new List<DiskInfo>();
        try
        {
            var failurePredictions = ReadFailurePredictStatus();
            var wearByIndex = ReadDiskWearByIndex();

            using var searcher = new ManagementObjectSearcher(
                "SELECT Index, Model, Size, MediaType, InterfaceType, Status, PNPDeviceID FROM Win32_DiskDrive");
            foreach (ManagementObject mo in searcher.Get())
            {
                long size = 0;
                try { size = System.Convert.ToInt64(mo["Size"] ?? 0L); } catch { /* leave 0 */ }

                int diskIndex = -1;
                try { diskIndex = System.Convert.ToInt32(mo["Index"] ?? -1); } catch { /* leave -1 */ }

                string wmiStatus = (mo["Status"] as string ?? string.Empty).Trim();
                string pnpDeviceId = (mo["PNPDeviceID"] as string ?? string.Empty).Trim();

                // MSStorageDriver_FailurePredictStatus (the SMART "is this drive about to die"
                // flag) is keyed by an InstanceName that starts with the same PNPDeviceID Win32_
                // DiskDrive reports, just normalized (spaces -> underscores, lowercased) - not an
                // exact match, so this is a prefix search rather than a dictionary lookup.
                bool? predictFailure = null;
                if (pnpDeviceId.Length > 0)
                {
                    var needle = NormalizeForMatch(pnpDeviceId);
                    foreach (var (instanceKey, value) in failurePredictions)
                    {
                        if (instanceKey.StartsWith(needle, StringComparison.Ordinal))
                        {
                            predictFailure = value;
                            break;
                        }
                    }
                }

                string health = predictFailure switch
                {
                    true => "Failure predicted",
                    false => "OK",
                    null => wmiStatus.Length == 0 ? "Unknown" : wmiStatus,
                };
                bool warning = predictFailure == true ||
                    (wmiStatus.Length > 0 && !wmiStatus.Equals("OK", StringComparison.OrdinalIgnoreCase));

                disks.Add(new DiskInfo
                {
                    Model = (mo["Model"] as string ?? "Unknown disk").Trim(),
                    SizeBytes = size,
                    MediaType = (mo["MediaType"] as string ?? string.Empty).Trim(),
                    InterfaceType = (mo["InterfaceType"] as string ?? string.Empty).Trim(),
                    HealthStatus = health,
                    IsHealthWarning = warning,
                    WearPercent = diskIndex >= 0 && wearByIndex.TryGetValue(diskIndex, out var wear) ? wear : null,
                    Index = diskIndex,
                });
            }
        }
        catch
        {
            // return whatever was gathered before the failure
        }
        return disks;
    }

    /// <summary>
    /// Reads the SMART failure-prediction flag from the storage miniport driver, via the legacy
    /// root\wmi MSStorageDriver_FailurePredictStatus class. Deliberately never throws past this
    /// method - the class isn't implemented by every driver (NVMe and some AHCI/RAID drivers omit
    /// it, and it can require running elevated, which this app already does), so disk health falls
    /// back to Win32_DiskDrive.Status alone when it's unavailable, the same graceful-degradation
    /// pattern SensorMonitorService uses for its own optional data source.
    /// </summary>
    private static List<(string InstanceKey, bool PredictFailure)> ReadFailurePredictStatus()
    {
        var result = new List<(string, bool)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi",
                "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus");
            foreach (ManagementObject mo in searcher.Get())
            {
                string instanceName = (mo["InstanceName"] as string ?? string.Empty).Trim();
                if (instanceName.Length == 0) continue;

                bool predictFailure = false;
                try { predictFailure = System.Convert.ToBoolean(mo["PredictFailure"] ?? false); } catch { /* leave false */ }

                result.Add((NormalizeForMatch(instanceName), predictFailure));
            }
        }
        catch
        {
            // Not available on this system/driver - callers treat an empty list as "unknown".
        }
        return result;
    }

    private static string NormalizeForMatch(string s) => s.Replace(' ', '_').ToLowerInvariant();

    /// <summary>
    /// SSD wear/life-used percentage (#65), via the Storage Management API's
    /// MSFT_StorageReliabilityCounter.Wear in root\Microsoft\Windows\Storage - the same figure
    /// PowerShell's Get-PhysicalDisk | Get-StorageReliabilityCounter reports. There's no direct
    /// WMI association between Win32_DiskDrive (used for the rest of this disk info) and
    /// MSFT_PhysicalDisk/MSFT_StorageReliabilityCounter, so this pairs them by their numeric
    /// index (Win32_DiskDrive.Index and MSFT_PhysicalDisk.DeviceId are both small integers "0",
    /// "1", ... assigned in enumeration order) - a best-effort match that holds on an ordinary
    /// single-controller desktop/laptop but isn't guaranteed on every RAID/Storage Spaces
    /// configuration, which is why this whole feature degrades to "not shown" rather than a wrong
    /// disk's wear value on any failure (this namespace is also simply unsupported by a fair
    /// number of SATA/AHCI drivers, same as the SMART failure-prediction class above).
    /// </summary>
    private static Dictionary<int, int> ReadDiskWearByIndex()
    {
        var result = new Dictionary<int, int>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage", "SELECT DeviceId, Wear FROM MSFT_StorageReliabilityCounter");
            foreach (ManagementObject mo in searcher.Get())
            {
                if (mo["DeviceId"] is not string deviceId || !int.TryParse(deviceId, out int index)) continue;
                if (mo["Wear"] is null) continue; // not reported by this drive's driver
                result[index] = System.Convert.ToInt32(mo["Wear"]);
            }
        }
        catch
        {
            // Namespace/class unavailable (older Windows, unsupported driver, or a locked-down
            // policy) - every disk simply shows no wear figure.
        }
        return result;
    }

    // MSFT_StorageReliabilityCounter fields that are internal bookkeeping rather than something a
    // user reading a SMART table would recognize/care about - dropped from the on-demand detail
    // view below rather than shown as noise.
    private static readonly HashSet<string> SmartDetailNoiseFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "DeviceId", "PSComputerName", "CimClass", "CimInstanceProperties", "CimSystemProperties",
    };

    /// <summary>Lightweight disk index+model list (round 9, #38) for populating the Storage tab's
    /// on-demand SMART-details disk picker without pulling in the rest of Query()'s (much heavier)
    /// full inventory read.</summary>
    public static List<(int Index, string Model)> ListDisksForSmart()
    {
        var result = new List<(int, string)>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Index, Model FROM Win32_DiskDrive");
            foreach (ManagementObject mo in searcher.Get())
            {
                int index = -1;
                try { index = System.Convert.ToInt32(mo["Index"] ?? -1); } catch { /* skip */ }
                if (index < 0) continue;
                result.Add((index, (mo["Model"] as string ?? $"Disk {index}").Trim()));
            }
        }
        catch
        {
            // return whatever was gathered before the failure
        }
        return result.OrderBy(d => d.Item1).ToList();
    }

    /// <summary>
    /// On-demand full SMART attribute table (round 9, #38), extending ReadDiskWearByIndex above to
    /// surface every other field MSFT_StorageReliabilityCounter reports for one disk (Temperature,
    /// ReadErrorsTotal/Uncorrected, PowerOnHours, StartStopCycleCount, Wear, ...) instead of just
    /// Wear alone. Rather than hardcode the exact property list (which varies by driver - some
    /// report FlashEraseCount and LoadUnloadCycleCount, others don't), this enumerates whatever
    /// non-null properties the instance actually carries, splitting each PascalCase name into
    /// words for display - the same "adaptive, don't assume a fixed schema" tradeoff
    /// BootPerformanceService's event-field scan already takes for a similarly loosely-documented
    /// data source. Same index-based disk pairing and empty-list-on-failure degradation as
    /// ReadDiskWearByIndex - deliberately on-demand (called from a button, not Query()) since it's
    /// only useful when actively investigating one specific disk.
    /// </summary>
    public static List<(string Label, string Value)> ReadSmartDetails(int diskIndex)
    {
        var result = new List<(string, string)>();
        if (diskIndex < 0) return result;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT * FROM MSFT_StorageReliabilityCounter WHERE DeviceId = '{diskIndex}'");
            foreach (ManagementObject mo in searcher.Get())
            {
                foreach (var prop in mo.Properties)
                {
                    if (prop.Value is null) continue;
                    if (SmartDetailNoiseFields.Contains(prop.Name)) continue;

                    string valueText = prop.Value is Array arr
                        ? string.Join(", ", arr.Cast<object>())
                        : Convert.ToString(prop.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    if (valueText.Length == 0) continue;

                    result.Add((SplitPascalCase(prop.Name), valueText));
                }
                break; // DeviceId is unique per disk - only one instance expected.
            }
        }
        catch
        {
            // Namespace/class unavailable, or this driver doesn't report reliability counters at
            // all - an empty list, shown as "No SMART data available" by the caller.
        }
        return result;
    }

    private static readonly Regex PascalCaseSplitRegex = new(@"(?<!^)(?=[A-Z])", RegexOptions.Compiled);
    private static string SplitPascalCase(string name) => PascalCaseSplitRegex.Replace(name, " ");

    // Publishers that are effectively OS/runtime components, not something a user "installed" in
    // the sense of "did this correlate with when my problem started" - filtered out the same way
    // ReadOutdatedDrivers excludes Microsoft/Generic/Standard-manufacturer noise.
    private static readonly string[] NoiseInstallPublishers = { "Microsoft Corporation", "Microsoft" };

    /// <summary>
    /// Recently installed third-party software (#68), from the per-app Uninstall registry keys
    /// (both native and the 32-bit view on a 64-bit OS) - the same data Windows' own "Installed
    /// apps" settings page reads. InstallDate is stored as a plain "yyyyMMdd" string (not a real
    /// registry date type), so this parses that exact format and skips anything that doesn't
    /// match rather than guessing. Windows keeps no equivalent log of *uninstalled* software, so
    /// this is deliberately install-only, not a full add/remove timeline.
    /// </summary>
    private static List<InstalledSoftwareInfo> ReadRecentlyInstalledSoftware()
    {
        var results = new List<InstalledSoftwareInfo>();
        string[] uninstallKeyPaths =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var keyPath in uninstallKeyPaths)
        {
            try
            {
                using var uninstallKey = Registry.LocalMachine.OpenSubKey(keyPath);
                if (uninstallKey is null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = uninstallKey.OpenSubKey(subKeyName);
                        if (sub is null) continue;

                        string name = (sub.GetValue("DisplayName") as string ?? string.Empty).Trim();
                        if (name.Length == 0) continue;
                        // Uninstall entries for OS patches/components have no DisplayIcon/real UI
                        // presence but do have a DisplayName - SystemComponent=1 is the documented
                        // flag for "don't show this in Add/Remove Programs", the same filter
                        // Explorer's own uninstall list applies.
                        if (sub.GetValue("SystemComponent") is int sc && sc == 1) continue;

                        string dateRaw = (sub.GetValue("InstallDate") as string ?? string.Empty).Trim();
                        if (dateRaw.Length != 8 || !DateTime.TryParseExact(dateRaw, "yyyyMMdd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var installDate))
                            continue;

                        // Older than ~6 months isn't "recent" for correlating with a fresh problem.
                        if (installDate < DateTime.Now.AddMonths(-6)) continue;

                        string publisher = (sub.GetValue("Publisher") as string ?? string.Empty).Trim();
                        if (NoiseInstallPublishers.Contains(publisher, StringComparer.OrdinalIgnoreCase)) continue;

                        results.Add(new InstalledSoftwareInfo { Name = name, Publisher = publisher, InstallDate = installDate });
                    }
                    catch
                    {
                        // One malformed subkey shouldn't stop the rest of the scan.
                    }
                }
            }
            catch
            {
                // Registry hive/path unavailable - fall through to whatever the other path found.
            }
        }

        return results
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()) // native+Wow6432 duplicates
            .OrderByDescending(r => r.InstallDate)
            .Take(20)
            .ToList();
    }

    // Device classes/statuses common enough on a healthy system that listing every instance
    // would just be noise - USB hubs and composite devices report "OK" constantly and add nothing
    // a user would act on; the useful signal here is specifically the ones that AREN'T "OK".
    private static List<UsbDeviceInfo> ReadUsbDevices()
    {
        var devices = new List<UsbDeviceInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Status, ConfigManagerErrorCode, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB%'");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = (mo["Name"] as string ?? string.Empty).Trim();
                if (name.Length == 0) continue;

                int errorCode = 0;
                try { errorCode = System.Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0); } catch { /* leave 0 */ }

                devices.Add(new UsbDeviceInfo
                {
                    Name = name,
                    Status = (mo["Status"] as string ?? "Unknown").Trim(),
                    ConfigManagerErrorCode = errorCode,
                });
            }
        }
        catch
        {
            // return whatever was gathered before the failure
        }
        return devices.OrderByDescending(d => d.HasError).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Page file drive letter and whether that drive is a fixed disk or an HDD/SSD (#70), via the
    /// documented MSFT_Volume -&gt; MSFT_Partition -&gt; MSFT_Disk -&gt; (indirectly) MediaType
    /// associator chain in root\Microsoft\Windows\Storage. A page file left on a slower secondary
    /// HDD on a system that otherwise boots from SSD (or the reverse) is a common, easy-to-miss
    /// slowdown cause on multi-drive systems. Degrades to null on any failure - this namespace can
    /// legitimately be unavailable the same way the SMART/reliability-counter classes above can.
    /// </summary>
    private static PageFileLocationInfo? ReadPageFileLocation()
    {
        try
        {
            string pageFilePath = string.Empty;
            using (var pfSearcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PageFileUsage"))
            {
                foreach (ManagementObject mo in pfSearcher.Get())
                {
                    pageFilePath = (mo["Name"] as string ?? string.Empty).Trim();
                    if (pageFilePath.Length > 0) break;
                }
            }
            if (pageFilePath.Length < 2) return null;

            string driveLetter = pageFilePath.Substring(0, 2); // e.g. "C:"
            string bootDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").ToUpperInvariant();

            using var volSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT ObjectId FROM MSFT_Volume WHERE DriveLetter = '{driveLetter[0]}'");
            foreach (ManagementObject vol in volSearcher.Get())
            {
                string mediaType = "Unknown";
                try
                {
                    using var partitions = new ManagementObjectSearcher(
                        @"root\Microsoft\Windows\Storage",
                        $"ASSOCIATORS OF {{MSFT_Volume.ObjectId='{EscapeWmiPath((string)vol["ObjectId"])}'}} WHERE AssocClass=MSFT_PartitionToVolume");
                    foreach (ManagementObject partition in partitions.Get())
                    {
                        using var disks = new ManagementObjectSearcher(
                            @"root\Microsoft\Windows\Storage",
                            $"ASSOCIATORS OF {{MSFT_Partition.ObjectId='{EscapeWmiPath((string)partition["ObjectId"])}'}} WHERE AssocClass=MSFT_PartitionToDisk");
                        foreach (ManagementObject disk in disks.Get())
                        {
                            mediaType = ReadPhysicalDiskMediaType(disk);
                            break;
                        }
                    }
                }
                catch { /* leave "Unknown" */ }

                return new PageFileLocationInfo
                {
                    DriveLetter = driveLetter,
                    MediaType = mediaType,
                    IsSameAsBootDrive = driveLetter.Equals(bootDrive, StringComparison.OrdinalIgnoreCase),
                };
            }
        }
        catch
        {
            // Storage Management API namespace unavailable - "Unknown" location, not a guess.
        }
        return null;
    }

    // MSFT_Disk itself doesn't expose a plain SSD/HDD media-type string reliably across drivers -
    // MSFT_PhysicalDisk.MediaType (via the disk's associated physical disk) is the actual documented
    // source for that, so this reads it as a second associator hop rather than guessing from the model name.
    private static string ReadPhysicalDiskMediaType(ManagementObject disk)
    {
        try
        {
            using var physicalDisks = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"ASSOCIATORS OF {{MSFT_Disk.ObjectId='{EscapeWmiPath((string)disk["ObjectId"])}'}} WHERE AssocClass=MSFT_DiskToPhysicalDisk");
            foreach (ManagementObject phys in physicalDisks.Get())
            {
                if (phys["MediaType"] is null) continue;
                return System.Convert.ToInt32(phys["MediaType"]) switch
                {
                    3 => "HDD",
                    4 => "SSD",
                    5 => "SCM",
                    _ => "Unknown",
                };
            }
        }
        catch { /* fall through */ }
        return "Unknown";
    }

    private static string EscapeWmiPath(string objectId) => objectId.Replace(@"\", @"\\").Replace("\"", "\\\"");

    /// <summary>Per-drive-letter free space, for a low-disk-space warning list. DriveInfo (not
    /// WMI) since it already handles removable/unready drives cleanly via IsReady. Round 9 adds
    /// four more per-volume facts (BitLocker, Recycle Bin size, VSS usage, TRIM status) - see
    /// VolumeDiagnosticsService for each one's own degradation story.</summary>
    private static async Task<List<VolumeInfo>> ReadVolumesAsync()
    {
        var volumes = new List<VolumeInfo>();
        var shadowUsageByVolume = await VolumeDiagnosticsService.ReadShadowCopyUsageByVolumeAsync();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;

            try
            {
                string driveLetter = drive.Name.TrimEnd('\\'); // e.g. "C:"
                string mediaType = drive.DriveType == DriveType.Fixed ? DiskFragmentationService.GetMediaType(driveLetter) : "Unknown";
                // TRIM is only a meaningful question for an SSD volume - same "hidden for the
                // media type it doesn't apply to" pattern HDD fragmentation already uses in
                // reverse.
                bool? trimEnabled = mediaType == "SSD" ? await VolumeDiagnosticsService.ReadTrimStatusAsync(driveLetter) : null;

                volumes.Add(new VolumeInfo
                {
                    Name = drive.Name,
                    Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.DriveType.ToString() : drive.VolumeLabel,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.TotalFreeSpace,
                    IsDirty = ReadVolumeDirtyBit(drive.Name),
                    MediaType = mediaType,
                    BitLockerStatus = VolumeDiagnosticsService.ReadBitLockerStatus(driveLetter),
                    RecycleBinBytes = VolumeDiagnosticsService.ReadRecycleBinBytes(drive.Name),
                    ShadowCopyBytes = shadowUsageByVolume.TryGetValue(driveLetter.TrimEnd(':'), out var used) ? used : null,
                    TrimEnabled = trimEnabled,
                });
            }
            catch
            {
                // Drive became unready between IsReady check and property reads - skip it.
            }
        }
        return volumes;
    }

    /// <summary>
    /// TPM / Secure Boot / VBS posture - three unrelated data sources bundled into one card
    /// because together they answer one question: "is this PC's security baseline configured the
    /// way Windows 11 expects." Each is wrapped independently since they fail differently: Secure
    /// Boot's registry key is readable unelevated; Win32_Tpm (root\cimv2\security\microsofttpm)
    /// needs the elevation this app already runs with, and can still be denied by a stricter local
    /// policy; Win32_DeviceGuard (root\Microsoft\Windows\DeviceGuard) is unelevated-readable but
    /// simply doesn't exist pre-Windows 10 1607.
    /// </summary>
    private static SecurityInfo ReadSecurityInfo(string? hypervisorLaunchType)
    {
        var (vbsPolicy, hvciPolicy, hvciScenario) = ReadDeviceGuardRegistry();
        return new SecurityInfo
        {
            SecureBootEnabled = ReadSecureBootEnabled(),
            TpmPresent = ReadTpmStatus(out var tpmReady, out var tpmVersion),
            TpmReady = tpmReady,
            TpmVersion = tpmVersion,
            VbsRunning = ReadVbsStatus(out var vbsServices),
            VbsServicesRunning = vbsServices,
            HypervisorLaunchType = hypervisorLaunchType,
            VbsPolicyEnabled = vbsPolicy,
            HvciPolicyEnabled = hvciPolicy,
            HvciScenarioEnabled = hvciScenario,
        };
    }

    /// <summary>
    /// #732: the "configured" half of VBS/HVCI posture, from the two registry locations Windows
    /// itself writes to depending on how VBS/Memory Integrity was turned on - the legacy
    /// HypervisorEnforcedCodeIntegrity value (set by Group Policy's "Turn On Virtualization Based
    /// Security" / older manual registry configuration) and the newer per-scenario Enabled value
    /// the Windows Security app's Core Isolation > Memory Integrity toggle actually writes. Both
    /// are read independently - a machine may have only one of the two set - and combined with the
    /// *running* half (Win32_DeviceGuard.SecurityServicesRunning, already read by ReadVbsStatus)
    /// in SystemSpecsViewModel.Apply, since "configured" and "running" commonly disagree (a policy
    /// change that needs a reboot to take effect).
    /// </summary>
    private static (bool? VbsPolicyEnabled, bool? HvciPolicyEnabled, bool? HvciScenarioEnabled) ReadDeviceGuardRegistry()
    {
        bool? vbsPolicy = ReadRegistryDwordAsBool(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity");
        bool? hvciPolicy = ReadRegistryDwordAsBool(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "HypervisorEnforcedCodeIntegrity");
        bool? hvciScenario = ReadRegistryDwordAsBool(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
        return (vbsPolicy, hvciPolicy, hvciScenario);
    }

    private static bool? ReadRegistryDwordAsBool(string subKeyPath, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKeyPath);
            return key?.GetValue(valueName) is int i ? i != 0 : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadSecureBootEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            var raw = key?.GetValue("UEFISecureBootEnabled");
            if (raw is int i) return i != 0;
        }
        catch
        {
            // Key absent (legacy BIOS boot, or the value simply isn't there) - "Unknown", not "off".
        }
        return null;
    }

    private static bool? ReadTpmStatus(out bool? ready, out string version)
    {
        ready = null;
        version = string.Empty;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\cimv2\security\microsofttpm",
                "SELECT IsActivated_InitialValue, IsEnabled_InitialValue, IsOwned_InitialValue, SpecVersion FROM Win32_Tpm");
            foreach (ManagementObject mo in searcher.Get())
            {
                bool activated = mo["IsActivated_InitialValue"] is bool a && a;
                bool enabled = mo["IsEnabled_InitialValue"] is bool e && e;
                bool owned = mo["IsOwned_InitialValue"] is bool o && o;
                ready = activated && enabled && owned;
                version = (mo["SpecVersion"] as string ?? string.Empty).Trim();
                return true; // A Win32_Tpm instance exists at all, i.e. a TPM chip is present.
            }
            return false; // Query succeeded but returned no instance - no TPM.
        }
        catch
        {
            // Most common cause: the querying process isn't elevated enough / a local policy
            // denies WMI access to this namespace even when elevated - "Unknown", not "absent".
            return null;
        }
    }

    private static bool? ReadVbsStatus(out IReadOnlyList<string> servicesRunning)
    {
        servicesRunning = Array.Empty<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard");
            foreach (ManagementObject mo in searcher.Get())
            {
                int status = Convert.ToInt32(mo["VirtualizationBasedSecurityStatus"] ?? 0);
                if (mo["SecurityServicesRunning"] is uint[] running)
                    servicesRunning = running.Select(SecurityServiceName).Where(n => n.Length > 0).ToList();
                return status == 2; // 0=Off, 1=Configured but not running, 2=Running
            }
        }
        catch
        {
            // Class doesn't exist on this OS build, or WMI is unavailable - "Unknown".
        }
        return null;
    }

    // Win32_DeviceGuard.SecurityServicesRunning value -> display name (documented enum, not every
    // value is user-relevant - unlisted values are simply dropped rather than shown as a number).
    private static string SecurityServiceName(uint code) => code switch
    {
        1 => "Credential Guard",
        2 => "Memory Integrity (HVCI)",
        3 => "System Guard Secure Launch",
        4 => "SMM Firmware Measurement",
        _ => string.Empty,
    };

    /// <summary>
    /// #733: firmware boot mode (UEFI vs legacy BIOS) and the system disk's partition style - a
    /// legacy/MBR system disk is a hard blocker for both Windows 11 and Secure Boot, so this is
    /// stated plainly (see FirmwareDiskInfo.IsHardBlocker) rather than left for the user to infer.
    /// </summary>
    private static FirmwareDiskInfo ReadFirmwareDiskInfo() => new()
    {
        FirmwareType = ReadFirmwareType(),
        SystemDiskPartitionStyle = ReadSystemDiskPartitionStyle(),
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFirmwareType(out uint firmwareType);

    /// <summary>
    /// GetFirmwareType (documented since Windows 8) is the purpose-built, "known Windows API"
    /// answer to "is this a UEFI or legacy BIOS boot" - it returns FirmwareTypeUefi (2) or
    /// FirmwareTypeBios (1) directly, which is the modern equivalent of the older
    /// GetFirmwareEnvironmentVariable-returns-ERROR_INVALID_FUNCTION probe technique (BIOS has no
    /// UEFI runtime services to even attempt that call against). Falls back to the SecureBoot\State
    /// registry key - a key only ever created on a UEFI boot, regardless of the
    /// UEFISecureBootEnabled value inside it - if the API call itself fails for any reason.
    /// </summary>
    private static string ReadFirmwareType()
    {
        try
        {
            if (GetFirmwareType(out uint type) != 0)
            {
                return type switch
                {
                    2 => "UEFI",
                    1 => "Legacy BIOS",
                    _ => "Unknown",
                };
            }
        }
        catch
        {
            // Fall through to the registry-based fallback below.
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            return key is not null ? "UEFI" : "Legacy BIOS (assumed - no SecureBoot state key present)";
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>MSFT_Disk.PartitionStyle (root\Microsoft\Windows\Storage) for the disk hosting the
    /// currently running OS (IsBoot), falling back to the disk hosting the system/EFI partition
    /// (IsSystem) and then simply the first enumerated disk if neither flag is set (some older
    /// storage stacks don't populate them) - PartitionStyle 1 = MBR, 2 = GPT.</summary>
    private static string ReadSystemDiskPartitionStyle()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT PartitionStyle, IsBoot, IsSystem FROM MSFT_Disk");

            ManagementObject? bootDisk = null, systemDisk = null, firstDisk = null;
            foreach (ManagementObject mo in searcher.Get())
            {
                firstDisk ??= mo;
                if (mo["IsBoot"] is bool ib && ib) { bootDisk = mo; break; }
                if (systemDisk is null && mo["IsSystem"] is bool isys && isys) systemDisk = mo;
            }

            var chosen = bootDisk ?? systemDisk ?? firstDisk;
            if (chosen is null) return "Unknown";

            int style = Convert.ToInt32(chosen["PartitionStyle"] ?? -1);
            return style switch
            {
                1 => "MBR",
                2 => "GPT",
                _ => "Unknown",
            };
        }
        catch
        {
            // root\Microsoft\Windows\Storage unavailable (older Windows, or the Storage
            // Management WMI provider isn't running) - Unknown, never guessed.
            return "Unknown";
        }
    }

    // The device classes where a stale third-party driver is actually a plausible troubleshooting
    // lead (GPU, network, storage controller, audio/webcam peripherals, ...) - deliberately not
    // "every PnP driver on the system", which is dominated by generic in-box Windows class drivers.
    private static readonly HashSet<string> InterestingDriverClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Display", "Net", "HDC", "SCSIAdapter", "Media", "Monitor", "USB", "Bluetooth", "Image", "Printer",
    };

    /// <summary>
    /// Flags third-party drivers old enough to be worth checking for an update. Two false-positive
    /// traps found while building this against Win32_PnPSignedDriver on a real machine, both
    /// filtered out here rather than shown: (1) most in-box/class drivers report a DriverVersion
    /// tied to the current OS build but a DriverDate frozen at the classic Windows placeholder
    /// (2006-06-21) even when they're perfectly current - filtering to non-generic manufacturers in
    /// InterestingDriverClasses removes nearly all of these; (2) filtering by DeviceClass alone
    /// still let a few "Generic ..." / "Standard ..." manufacturer entries with that same 2006
    /// placeholder date through, so those are excluded explicitly too, on top of an outright
    /// exclusion of that exact placeholder year. What's left is real vendor drivers (GPU, NIC,
    /// audio/webcam peripherals, ...) with real dates - flagged when older than 2 years, a
    /// deliberately conservative bar to keep this list short and trustworthy rather than noisy.
    /// </summary>
    private static List<DriverInfo> ReadOutdatedDrivers()
    {
        var drivers = new List<DriverInfo>();
        try
        {
            var cutoff = DateTime.Now.AddYears(-2);

            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceName, Manufacturer, DriverVersion, DriverDate, DeviceClass FROM Win32_PnPSignedDriver");
            foreach (ManagementObject mo in searcher.Get())
            {
                string deviceClass = (mo["DeviceClass"] as string ?? string.Empty).Trim();
                if (!InterestingDriverClasses.Contains(deviceClass)) continue;

                string manufacturer = (mo["Manufacturer"] as string ?? string.Empty).Trim();
                if (manufacturer.Length == 0 ||
                    manufacturer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                    manufacturer.Contains("Generic", StringComparison.OrdinalIgnoreCase) ||
                    manufacturer.Contains("Standard", StringComparison.OrdinalIgnoreCase))
                    continue;

                string deviceName = (mo["DeviceName"] as string ?? string.Empty).Trim();
                if (deviceName.Length == 0) continue;

                DateTime? driverDate = null;
                if (mo["DriverDate"] is string wmiDate)
                {
                    try { driverDate = ManagementDateTimeConverter.ToDateTime(wmiDate); } catch { /* leave null */ }
                }
                // The classic in-box-driver placeholder date - never a real vendor release date.
                if (driverDate is null || driverDate.Value.Year <= 2006) continue;
                if (driverDate.Value > cutoff) continue;

                drivers.Add(new DriverInfo
                {
                    DeviceName = deviceName,
                    Manufacturer = manufacturer,
                    DriverVersion = (mo["DriverVersion"] as string ?? string.Empty).Trim(),
                    DriverDate = driverDate,
                });
            }
        }
        catch
        {
            // return whatever was gathered before the failure
        }
        return drivers.OrderBy(d => d.DriverDate).Take(20).ToList();
    }

    /// <summary>
    /// Recently installed Windows updates/hotfixes (#57), via Win32_QuickFixEngineering - a huge,
    /// common source of "PC got slow/broke after X" reports. Same try/catch-degrades-to-empty
    /// shape as ReadOutdatedDrivers; this WMI class can legitimately return nothing on some
    /// builds even though updates were installed (Windows doesn't guarantee every update is
    /// recorded here), so an empty list means "nothing to show", not "no updates installed".
    /// </summary>
    /// <summary>#780: internal (not private) so WindowsUpdateUninstallService can reuse this exact
    /// same QFE read for its removable-updates list, rather than a second near-identical WMI query.</summary>
    internal static List<UpdateInfo> ReadRecentHotfixes()
    {
        var updates = new List<UpdateInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering");
            foreach (ManagementObject mo in searcher.Get())
            {
                string hotFixId = (mo["HotFixID"] as string ?? string.Empty).Trim();
                if (hotFixId.Length == 0) continue;

                DateTime? installedOn = null;
                // InstalledOn comes back as a plain culture-formatted date string (not the usual
                // WMI CIM_DATETIME format) for this particular class - parse it directly.
                if (mo["InstalledOn"] is string raw && DateTime.TryParse(raw, out var parsed))
                    installedOn = parsed;

                updates.Add(new UpdateInfo
                {
                    HotFixId = hotFixId,
                    Description = (mo["Description"] as string ?? string.Empty).Trim(),
                    InstalledOn = installedOn,
                });
            }
        }
        catch
        {
            // return whatever was gathered before the failure
        }
        return updates.OrderByDescending(u => u.InstalledOn).Take(15).ToList();
    }

    /// <summary>
    /// Registered antivirus/security products (#63), via the SecurityCenter2 WMI namespace.
    /// productState is an undocumented (but widely reverse-engineered) bitmask; the middle byte
    /// generally reflects real-time-protection enabled/disabled status. This is a best-effort
    /// heuristic, not a verified fact - the same "quick visual flag, not a security verdict"
    /// tradeoff ProcessMonitorService's signature check documents - so it's presented as such
    /// rather than a precise on/off state.
    /// </summary>
    private static List<AntivirusInfo> ReadAntivirusProducts(out bool multipleActive)
    {
        var products = new List<AntivirusInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = (mo["displayName"] as string ?? string.Empty).Trim();
                if (name.Length == 0) continue;

                int state = 0;
                try { state = Convert.ToInt32(mo["productState"] ?? 0); } catch { /* leave 0 */ }
                // Middle byte: 0x10/0x11 families generally mean "enabled", 0x00/0x01 mean "off" -
                // approximate, see the method comment above.
                bool looksEnabled = ((state >> 8) & 0xFF) is 0x10 or 0x11 or 0x12;

                products.Add(new AntivirusInfo { Name = name, LooksEnabled = looksEnabled });
            }
        }
        catch
        {
            // SecurityCenter2 namespace unavailable (older Windows, or a locked-down policy) -
            // "no products detected", not an error.
        }

        multipleActive = products.Count(p => p.LooksEnabled) > 1;
        return products;
    }

    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint FsctlIsVolumeDirty = 0x00090078;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>
    /// File system dirty bit (#29) via FSCTL_IS_VOLUME_DIRTY - the same flag `fsutil dirty query`
    /// reports, set when the volume needs a chkdsk pass (typically after an unclean shutdown).
    /// Needs a handle to the raw volume (`\\.\C:`, no trailing backslash) rather than a file
    /// path - wrapped to degrade to null ("Unknown") on any failure, same tier as the SMART
    /// failure-prediction lookup above.
    /// </summary>
    private static bool? ReadVolumeDirtyBit(string driveName)
    {
        string trimmed = driveName.TrimEnd('\\');
        if (trimmed.Length < 2) return null;

        try
        {
            using var handle = CreateFile($@"\\.\{trimmed}", GenericRead, FileShareReadWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) return null;

            var outBuffer = Marshal.AllocHGlobal(1);
            try
            {
                bool ok = DeviceIoControl(handle, FsctlIsVolumeDirty, IntPtr.Zero, 0, outBuffer, 1, out _, IntPtr.Zero);
                if (!ok) return null;
                int flags = Marshal.ReadByte(outBuffer);
                return (flags & 0x1) != 0; // VOLUME_IS_DIRTY bit
            }
            finally
            {
                Marshal.FreeHGlobal(outBuffer);
            }
        }
        catch
        {
            // Requires access to the raw volume - can fail even elevated on some configurations
            // (e.g. a removable/network drive) - "Unknown" rather than a false "clean".
            return null;
        }
    }

    /// <summary>#57: chassis/form-factor readout, via Win32_SystemEnclosure.ChassisTypes - a real,
    /// documented SMBIOS-backed enum (the first reported code is used; a chassis rarely reports more
    /// than one meaningful type). Unrecognized/unlisted codes fall back to "Unknown" rather than a
    /// guess.</summary>
    private static string ReadChassisType()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementObject mo in searcher.Get())
            {
                if (mo["ChassisTypes"] is ushort[] { Length: > 0 } types)
                    return ChassisTypeName(types[0]);
            }
        }
        catch
        {
            // WMI class unavailable - "Unknown".
        }
        return "Unknown";
    }

    private static string ChassisTypeName(int code) => code switch
    {
        3 => "Desktop",
        4 => "Low Profile Desktop",
        5 => "Pizza Box",
        6 => "Mini Tower",
        7 => "Tower",
        8 => "Portable",
        9 => "Laptop",
        10 => "Notebook",
        11 => "Hand Held",
        12 => "Docking Station",
        13 => "All in One",
        14 => "Sub Notebook",
        15 => "Space-saving",
        16 => "Lunch Box",
        17 => "Main System Chassis",
        18 => "Expansion Chassis",
        21 => "Peripheral Chassis",
        23 => "Rack Mount Chassis",
        30 => "Tablet",
        31 => "Convertible",
        32 => "Detachable",
        _ => "Unknown",
    };

    /// <summary>#58: Windows edition (already surfaced via OsName's Caption) plus activation status,
    /// via the SoftwareLicensingProduct WMI class filtered to the Windows edition's ApplicationID -
    /// LicenseStatus 1 means "Licensed"/activated. Informational text only - no product key is ever
    /// read from this or any other class.</summary>
    private static string ReadActivationStatus()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL");
            foreach (ManagementObject mo in searcher.Get())
            {
                int status = Convert.ToInt32(mo["LicenseStatus"] ?? -1);
                return status switch
                {
                    1 => "Activated",
                    0 => "Not activated (unlicensed)",
                    _ => "Not activated",
                };
            }
        }
        catch
        {
            // SoftwareLicensingProduct can be restricted by policy on some editions - "Unknown".
        }
        return "Unknown";
    }

    /// <summary>#59: installed .NET runtime versions, via the shared-framework directory layout
    /// dotnet.exe itself uses (dotnet\shared\&lt;RuntimeName&gt;\&lt;Version&gt;) rather than a
    /// registry/WMI read - this is the same folder structure `dotnet --list-runtimes` reads. Checks
    /// both Program Files and Program Files (x86), since a 32-bit runtime install lands in the
    /// latter.</summary>
    private static List<string> ReadDotNetRuntimes()
    {
        var result = new List<string>();
        try
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct();

            foreach (var root in roots)
            {
                var sharedDir = Path.Combine(root, "dotnet", "shared");
                if (!Directory.Exists(sharedDir)) continue;

                foreach (var runtimeDir in Directory.GetDirectories(sharedDir))
                {
                    string runtimeName = Path.GetFileName(runtimeDir);
                    foreach (var versionDir in Directory.GetDirectories(runtimeDir))
                        result.Add($"{runtimeName} {Path.GetFileName(versionDir)}");
                }
            }
        }
        catch
        {
            // Best-effort filesystem scan - a permission issue just means an incomplete list.
        }
        return result.Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>#60: active display inventory. Resolution/refresh rate come from
    /// Win32_VideoController's current-mode fields (CurrentHorizontalResolution/
    /// CurrentVerticalResolution/CurrentRefreshRate) - a simpler, WMI-only source than parsing EDID
    /// or calling EnumDisplaySettings, at the cost of describing "the adapter's current output mode"
    /// rather than a true per-monitor EDID record. Connection type is a second, independent
    /// WMI source - root\wmi's WmiMonitorConnectionParams, which reports the documented
    /// D3DKMDT_VIDEO_OUTPUT_TECHNOLOGY enum per physical monitor. There is no public API mapping one
    /// source's rows to the other's, so - the same "only pair when the counts match, otherwise don't
    /// guess" rule GpuMonitorService uses for LUID-to-adapter pairing - resolution is only attached
    /// to a connection-type row when both lists report the same count; otherwise whichever source is
    /// available is still shown, just without the other's detail. HDR support has no reliable
    /// enumeration source short of DXGI/IDXGIOutput6 COM interop, so it's deliberately not
    /// included rather than guessed.</summary>
    private static List<MonitorInfo> ReadMonitors()
    {
        var connectionTypes = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT VideoOutputTechnology FROM WmiMonitorConnectionParams");
            foreach (ManagementObject mo in searcher.Get())
            {
                int tech = 0;
                try { tech = Convert.ToInt32(mo["VideoOutputTechnology"] ?? 0); } catch { /* leave 0 -> "VGA" bucket avoided below via Other fallback */ }
                connectionTypes.Add(VideoOutputTechnologyName(tech));
            }
        }
        catch
        {
            // root\wmi monitor classes unavailable (some VM/RDP configurations, or a locked-down
            // policy) - fall through to whatever Win32_VideoController resolution data can still be
            // shown below, just without a connection type.
        }

        var resolutions = new List<(int W, int H, int Hz)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController WHERE CurrentHorizontalResolution IS NOT NULL");
            foreach (ManagementObject mo in searcher.Get())
            {
                int w = Convert.ToInt32(mo["CurrentHorizontalResolution"] ?? 0);
                int h = Convert.ToInt32(mo["CurrentVerticalResolution"] ?? 0);
                int hz = Convert.ToInt32(mo["CurrentRefreshRate"] ?? 0);
                if (w > 0 && h > 0) resolutions.Add((w, h, hz));
            }
        }
        catch
        {
            // fall through - connectionTypes alone can still produce rows below
        }

        var result = new List<MonitorInfo>();
        bool canPairResolution = connectionTypes.Count > 0 && connectionTypes.Count == resolutions.Count;

        if (connectionTypes.Count > 0)
        {
            for (int i = 0; i < connectionTypes.Count; i++)
            {
                var res = canPairResolution ? resolutions[i] : ((int W, int H, int Hz)?)null;
                result.Add(new MonitorInfo
                {
                    Name = $"Display {i + 1}",
                    WidthPx = res?.W ?? 0,
                    HeightPx = res?.H ?? 0,
                    RefreshHz = res?.Hz ?? 0,
                    ConnectionType = connectionTypes[i],
                });
            }
        }
        else
        {
            for (int i = 0; i < resolutions.Count; i++)
            {
                result.Add(new MonitorInfo
                {
                    Name = $"Display {i + 1}",
                    WidthPx = resolutions[i].W,
                    HeightPx = resolutions[i].H,
                    RefreshHz = resolutions[i].Hz,
                    ConnectionType = "Unknown",
                });
            }
        }
        return result;
    }

    // D3DKMDT_VIDEO_OUTPUT_TECHNOLOGY - a documented Microsoft enum (d3dkmdt.h), not a guess.
    private static string VideoOutputTechnologyName(int code) => code switch
    {
        0 => "VGA",
        1 => "S-Video",
        2 => "Composite",
        3 => "Component",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS (internal)",
        8 => "D-JPN",
        9 => "SDI",
        10 => "DisplayPort",
        11 => "DisplayPort (embedded)",
        12 => "UDI",
        13 => "UDI (embedded)",
        14 => "SDTV dongle",
        15 => "Miracast",
        16 => "Indirect (wired)",
        17 => "Indirect (virtual)",
        -1 => "Other",
        int.MinValue => "Internal", // 0x80000000
        _ => "Unknown",
    };

    /// <summary>#61: motherboard chipset driver version. There is no single canonical "chipset
    /// driver" WMI class, so this searches Win32_PnPSignedDriver for System/SMBus-class devices
    /// whose name mentions "Chipset"/"SMBus"/"PCI Express Root" (the way Intel/AMD chipset INF
    /// packages typically name their entries) and reports the newest one found by driver date -
    /// best-effort, the same tier as the outdated-driver date filtering, degrading to "Unknown"
    /// rather than a guess when nothing matches.</summary>
    private static string ReadChipsetDriverInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceName, Manufacturer, DriverVersion, DriverDate, DeviceClass FROM Win32_PnPSignedDriver WHERE DeviceClass='System' OR DeviceClass='SMBus' OR DeviceClass='HDC'");

            DriverInfo? best = null;
            foreach (ManagementObject mo in searcher.Get())
            {
                string deviceName = (mo["DeviceName"] as string ?? string.Empty).Trim();
                if (deviceName.Length == 0) continue;
                bool looksLikeChipset =
                    deviceName.Contains("Chipset", StringComparison.OrdinalIgnoreCase) ||
                    deviceName.Contains("SMBus", StringComparison.OrdinalIgnoreCase) ||
                    deviceName.Contains("PCI Express Root", StringComparison.OrdinalIgnoreCase);
                if (!looksLikeChipset) continue;

                DateTime? date = null;
                if (mo["DriverDate"] is string wmiDate)
                {
                    try { date = ManagementDateTimeConverter.ToDateTime(wmiDate); } catch { /* leave null */ }
                }

                var candidate = new DriverInfo
                {
                    DeviceName = deviceName,
                    Manufacturer = (mo["Manufacturer"] as string ?? string.Empty).Trim(),
                    DriverVersion = (mo["DriverVersion"] as string ?? string.Empty).Trim(),
                    DriverDate = date,
                };
                if (best is null || (candidate.DriverDate ?? DateTime.MinValue) > (best.DriverDate ?? DateTime.MinValue))
                    best = candidate;
            }

            if (best is not null)
            {
                string dateText = best.DriverDate is { } d ? $" ({d:yyyy-MM-dd})" : string.Empty;
                return $"{best.DeviceName} — {(best.DriverVersion.Length > 0 ? best.DriverVersion : "Unknown version")}{dateText}";
            }
        }
        catch
        {
            // WMI query failed outright - "Unknown".
        }
        return "Unknown";
    }

    /// <summary>#63: Windows Defender exclusion list (Paths/Extensions/Processes/IpAddresses), a
    /// direct read-only registry read - the same key the Defender Security Center UI itself edits.
    /// Returns null (rendered as "Unknown/inaccessible") when the key can't be opened or enumerated
    /// at all - Tamper Protection can legitimately deny this even to this app's elevated process,
    /// and that's a materially different situation from "read successfully, found nothing".</summary>
    private static IReadOnlyList<string>? ReadDefenderExclusions()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Exclusions");
            if (root is null) return null;

            var result = new List<string>();
            foreach (var subKeyName in new[] { "Paths", "Extensions", "Processes", "IpAddresses" })
            {
                using var sub = root.OpenSubKey(subKeyName);
                if (sub is null) continue;
                string label = DefenderExclusionCategoryLabel(subKeyName);
                foreach (var valueName in sub.GetValueNames())
                    if (!string.IsNullOrWhiteSpace(valueName))
                        result.Add($"{label}: {valueName}");
            }
            return result;
        }
        catch
        {
            // Access denied (Tamper Protection / policy), even though this process is elevated -
            // "Unknown", not a false "no exclusions configured".
            return null;
        }
    }

    /// <summary>Singularized display label for a Defender exclusion registry subkey name. A plain
    /// `TrimEnd('s')` only strips one trailing 's', which mishandles "Processes" -> "Processe" and
    /// "IpAddresses" -> "IpAddresse" - so this is an explicit map for the four known categories,
    /// falling back to the raw subkey name (never a wrong guess) for anything this app hasn't
    /// seen before.</summary>
    private static string DefenderExclusionCategoryLabel(string subKeyName) => subKeyName switch
    {
        "Paths" => "Path",
        "Processes" => "Process",
        "Extensions" => "Extension",
        "IpAddresses" => "IP Address",
        _ => subKeyName,
    };

    /// <summary>#64: identifiers for a "copy hardware IDs" support-ticket helper - system product
    /// UUID (Win32_ComputerSystemProduct.UUID) and CPU ID (Win32_Processor.ProcessorId). Both are
    /// already-standard WMI fields with no personal/account data in them (unlike a Windows product
    /// key, which this app never reads anywhere).</summary>
    private static (string Uuid, string CpuId) ReadHardwareIdentifiers()
    {
        string uuid = string.Empty, cpuId = string.Empty;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
            foreach (ManagementObject mo in searcher.Get())
            {
                uuid = (mo["UUID"] as string ?? string.Empty).Trim();
                break;
            }
        }
        catch { /* leave blank */ }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor");
            foreach (ManagementObject mo in searcher.Get())
            {
                cpuId = (mo["ProcessorId"] as string ?? string.Empty).Trim();
                break;
            }
        }
        catch { /* leave blank */ }

        return (uuid, cpuId);
    }
}
