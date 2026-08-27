using System.IO;
using System.Management;
using System.Runtime.InteropServices;
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

    public SystemSpecs Query()
    {
        var (osName, osVersion, osArch, osInstallDate, osInstallAgeDays) = ReadOperatingSystem();
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
            OsInstallAgeDays = osInstallAgeDays,

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

            Security = ReadSecurityInfo(),
            OutdatedDrivers = ReadOutdatedDrivers(),
            RecentUpdates = ReadRecentHotfixes(),
            AntivirusProducts = ReadAntivirusProducts(out var multipleActive),
            MultipleActiveAvWarning = multipleActive,
        };
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
                    IsDirty = ReadVolumeDirtyBit(drive.Name),
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
    private static SecurityInfo ReadSecurityInfo() => new()
    {
        SecureBootEnabled = ReadSecureBootEnabled(),
        TpmPresent = ReadTpmStatus(out var tpmReady, out var tpmVersion),
        TpmReady = tpmReady,
        TpmVersion = tpmVersion,
        VbsRunning = ReadVbsStatus(out var vbsServices),
        VbsServicesRunning = vbsServices,
    };

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
    private static List<UpdateInfo> ReadRecentHotfixes()
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
}
