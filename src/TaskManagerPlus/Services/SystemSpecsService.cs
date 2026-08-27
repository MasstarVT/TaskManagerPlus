using System.IO;
using System.Management;
using Microsoft.Win32;
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

    public SystemSpecs Query()
    {
        var (osName, osVersion, osArch, osInstallDate) = ReadOperatingSystem();
        var (manufacturer, model, systemType) = ReadComputerSystem();
        var (boardManufacturer, boardProduct) = ReadBaseBoard();
        var biosVersion = ReadBios();
        var (cpuName, physicalCores, logicalProcessors, maxClockGhz) = ReadCpu();
        var memoryModules = ReadMemoryModules();
        long ramTotal = memoryModules.Sum(m => m.CapacityBytes);

        return new SystemSpecs
        {
            OsName = osName,
            OsVersion = osVersion,
            OsArchitecture = osArch,
            OsInstallDate = osInstallDate,

            ComputerName = Environment.MachineName,
            Manufacturer = manufacturer,
            Model = model,
            SystemType = systemType,

            MotherboardManufacturer = boardManufacturer,
            MotherboardProduct = boardProduct,
            BiosVersion = biosVersion,

            CpuName = cpuName,
            CpuPhysicalCores = physicalCores,
            CpuLogicalProcessors = logicalProcessors,
            CpuMaxClockGhz = maxClockGhz,

            RamTotalBytes = ramTotal,
            MemoryModules = memoryModules,

            Gpus = ReadGpus(),
            Disks = ReadDisks(),
            Volumes = ReadVolumes(),
        };
    }

    private static (string Name, string Version, string Architecture, string InstallDate) ReadOperatingSystem()
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
                if (mo["InstallDate"] is string wmiDate)
                {
                    try { installDate = ManagementDateTimeConverter.ToDateTime(wmiDate).ToShortDateString(); }
                    catch { /* leave blank */ }
                }
                return (name, version, arch, installDate);
            }
        }
        catch
        {
            // fall through to defaults
        }
        return ("Unknown OS", string.Empty, string.Empty, string.Empty);
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

    private static string ReadBios()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT SMBIOSBIOSVersion FROM Win32_BIOS");
            foreach (ManagementObject mo in searcher.Get())
                return (mo["SMBIOSBIOSVersion"] as string ?? string.Empty).Trim();
        }
        catch
        {
            // fall through to default
        }
        return string.Empty;
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
                "SELECT DeviceLocator, Capacity, Speed, Manufacturer, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            foreach (ManagementObject mo in searcher.Get())
            {
                long capacity = 0;
                try { capacity = System.Convert.ToInt64(mo["Capacity"] ?? 0L); } catch { /* leave 0 */ }

                double speed = 0;
                try { speed = System.Convert.ToDouble(mo["Speed"] ?? 0.0); } catch { /* leave 0 */ }

                int smBiosType = 0;
                try { smBiosType = System.Convert.ToInt32(mo["SMBIOSMemoryType"] ?? 0); } catch { /* leave 0 */ }

                modules.Add(new MemoryModuleInfo
                {
                    Location = (mo["DeviceLocator"] as string ?? "RAM").Trim(),
                    CapacityBytes = capacity,
                    SpeedMhz = speed,
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

            using var searcher = new ManagementObjectSearcher(
                "SELECT Model, Size, MediaType, InterfaceType, Status, PNPDeviceID FROM Win32_DiskDrive");
            foreach (ManagementObject mo in searcher.Get())
            {
                long size = 0;
                try { size = System.Convert.ToInt64(mo["Size"] ?? 0L); } catch { /* leave 0 */ }

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

    /// <summary>Per-drive-letter free space, for a low-disk-space warning list. DriveInfo (not
    /// WMI) since it already handles removable/unready drives cleanly via IsReady.</summary>
    private static List<VolumeInfo> ReadVolumes()
    {
        var volumes = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;

            try
            {
                volumes.Add(new VolumeInfo
                {
                    Name = drive.Name,
                    Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.DriveType.ToString() : drive.VolumeLabel,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.TotalFreeSpace,
                });
            }
            catch
            {
                // Drive became unready between IsReady check and property reads - skip it.
            }
        }
        return volumes;
    }
}
