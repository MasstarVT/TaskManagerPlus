using System.IO;
using System.Management;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, #377/#378/#382/#383: registry/WMI-only storage-controller facts - which driver the
/// boot-time controller is actually bound to, the storahci MSI/idle-power known-issue quick flag,
/// per-disk write-cache/power-protection facts, and the loaded storage-stack driver inventory +
/// PnP problem codes + disk I/O timeout. No raw interop needed for any of this (unlike #379/#381,
/// see StorageLinkService) - it's all Win32_PnPSignedDriver/Win32_PnPEntity plus the same
/// "HKLM\SYSTEM\CurrentControlSet\Enum\&lt;PNPDeviceID&gt;\Device Parameters\..." registry-read
/// pattern RemovalPolicyService already uses for a different per-device policy. Every read degrades
/// to Unknown/empty rather than fabricated - a denied key, an absent WMI class, or an unrecognized
/// driver name never gets shown as a guessed mode.
/// </summary>
public static class StorageControllerService
{
    // Driver *service* names (the value under Enum\&lt;devid&gt;\Service, not the .inf/.sys file
    // name a user might expect) that unambiguously mean each mode. Vendor RAID/RST driver names
    // deliberately aren't asserted as a hard "RAID" verdict - see StorageControllerMode's remarks -
    // because the same iaStorV/iaStorAC-family driver also binds in plain AHCI-mode systems on many
    // OEM laptops (it simply replaces storahci as a "better" driver Windows Update offers), so
    // "this vendor driver is bound" only proves "not the inbox AHCI driver", not "RAID BIOS mode".
    private static readonly HashSet<string> AhciServiceNames = new(StringComparer.OrdinalIgnoreCase) { "storahci" };
    private static readonly HashSet<string> LegacyIdeServiceNames = new(StringComparer.OrdinalIgnoreCase)
        { "atapi", "pciide", "pciidex", "intelide" };
    private static readonly HashSet<string> VendorRaidOrRstServiceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "iastorav", "iastorac", "iastora", "iastorv", "iastorvd", "iastorafs", "iastorb",
        "megasr", "nvraid", "nvstor", "amd_sata", "amd_xata", "rcraid", "rcbottom", "vsmraid",
    };

    // #383: fixed storage-stack drivers of interest beyond whatever adapter driver #377 finds bound.
    private static readonly string[] AdditionalInventoryServiceNames = { "storport", "disk", "partmgr", "volsnap", "stornvme" };

    /// <summary>Win32_DiskDrive.PNPDeviceID for one disk index - the same best-effort index pairing
    /// SmartRawAttributeService/RemovalPolicyService already rely on elsewhere, exposed here so
    /// #382 (and StorageLinkService's #379/#381) don't each run their own copy of this query.</summary>
    public static string? GetDiskPnpDeviceId(int diskIndex)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT PNPDeviceID FROM Win32_DiskDrive WHERE Index = {diskIndex}");
            foreach (ManagementObject mo in searcher.Get())
            {
                string id = (mo["PNPDeviceID"] as string ?? string.Empty).Trim();
                if (id.Length > 0) return id;
            }
        }
        catch { /* fall through to null */ }
        return null;
    }

    private sealed record AdapterDriverRow(string PnpDeviceId, string DeviceName, string? ServiceName, string Version, string DateText, string Signer);

    /// <summary>Win32_PnPSignedDriver rows for the SCSIAdapter/HDC/DiskDrive device classes, each
    /// paired with its bound driver *service* name read from the Enum key - the DeviceClass=
    /// SCSIAdapter/HDC entries are the actual SATA/NVMe/RAID host controllers #377/#378 care about;
    /// DiskDrive is included too since #383's inventory wants disk.sys's per-disk PnP entry as well.
    /// </summary>
    private static List<AdapterDriverRow> ReadAdapterDrivers()
    {
        var rows = new List<AdapterDriverRow>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, DeviceName, DeviceClass, DriverVersion, DriverDate, Signer FROM Win32_PnPSignedDriver " +
                "WHERE DeviceClass = 'SCSIAdapter' OR DeviceClass = 'HDC' OR DeviceClass = 'DiskDrive'");
            foreach (ManagementObject mo in searcher.Get())
            {
                string deviceId = (mo["DeviceID"] as string ?? string.Empty).Trim();
                if (deviceId.Length == 0) continue;

                string dateText = "Unknown";
                if (mo["DriverDate"] is string wmiDate && wmiDate.Length >= 8)
                {
                    // WMI datetime format (yyyyMMddHHmmss.ffffff+zzz) - just the date portion matters here.
                    try { dateText = ManagementDateTimeConverter.ToDateTime(wmiDate).ToString("d"); }
                    catch { /* leave "Unknown" */ }
                }

                rows.Add(new AdapterDriverRow(
                    PnpDeviceId: deviceId,
                    DeviceName: (mo["DeviceName"] as string ?? "Unknown device").Trim(),
                    ServiceName: ReadBoundServiceName(deviceId),
                    Version: (mo["DriverVersion"] as string ?? "Unknown").Trim(),
                    DateText: dateText,
                    Signer: (mo["Signer"] as string ?? "Unknown").Trim()));
            }
        }
        catch { /* WMI unavailable - empty list, caller degrades */ }
        return rows;
    }

    /// <summary>The driver service key name (e.g. "storahci", "iaStorAC") bound to one PnP device
    /// instance, per its Enum\&lt;devid&gt;\Service registry value - the precise, driver-stack-
    /// agnostic way to answer "which driver is actually loaded for this controller", rather than
    /// guessing from DeviceName/InfName text.</summary>
    private static string? ReadBoundServiceName(string pnpDeviceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{pnpDeviceId}");
            return key?.GetValue("Service") as string;
        }
        catch { return null; }
    }

    // ================================================================================
    // #377/#378/#383: system-wide controller facts, read once at Storage-tab load.
    // ================================================================================
    public static StorageControllerFacts ReadControllerFacts()
    {
        try
        {
            var adapters = ReadAdapterDrivers();
            var adapterOnly = adapters.Where(a => a.ServiceName is not null).ToList();

            string? boundService = null;
            var mode = StorageControllerMode.Unknown;
            AdapterDriverRow? boundAdapterRow = null;

            foreach (var row in adapterOnly)
            {
                if (row.ServiceName is null) continue;
                if (AhciServiceNames.Contains(row.ServiceName)) { mode = StorageControllerMode.Ahci; boundService = row.ServiceName; boundAdapterRow = row; break; }
            }
            if (mode == StorageControllerMode.Unknown)
            {
                foreach (var row in adapterOnly)
                {
                    if (row.ServiceName is null) continue;
                    if (LegacyIdeServiceNames.Contains(row.ServiceName)) { mode = StorageControllerMode.LegacyIde; boundService = row.ServiceName; boundAdapterRow = row; break; }
                }
            }
            if (mode == StorageControllerMode.Unknown)
            {
                foreach (var row in adapterOnly)
                {
                    if (row.ServiceName is null) continue;
                    if (VendorRaidOrRstServiceNames.Contains(row.ServiceName)) { mode = StorageControllerMode.VendorRaidOrRst; boundService = row.ServiceName; boundAdapterRow = row; break; }
                }
            }

            string modeDetail = mode switch
            {
                StorageControllerMode.Ahci =>
                    $"Storage controller \"{boundAdapterRow?.DeviceName}\" is bound to storahci (Windows' inbox AHCI driver) - AHCI mode is active.",
                StorageControllerMode.LegacyIde =>
                    $"Storage controller \"{boundAdapterRow?.DeviceName}\" is bound to {boundService} - a legacy IDE/PATA-compatibility driver. Quick flag, not a verdict: most chipsets built for AHCI-mode SATA silently disable NCQ and TRIM passthrough when running in legacy IDE compatibility mode - worth checking the BIOS/UEFI SATA mode setting if this wasn't intentional.",
                StorageControllerMode.VendorRaidOrRst =>
                    $"Storage controller \"{boundAdapterRow?.DeviceName}\" is bound to {boundService}, a vendor RAID/RST driver. This driver binds in both AHCI and RAID BIOS modes on many systems, so this alone doesn't confirm RAID is active - check the vendor's RAID/RST management utility or BIOS setup for the real mode.",
                _ => "Could not determine which driver is bound to the boot-time storage controller (no matching SCSIAdapter/HDC PnP entry, or its Service registry value wasn't readable).",
            };

            // #378: only meaningful once storahci is confirmed as the bound driver above.
            bool showMsiCheck = mode == StorageControllerMode.Ahci && boundAdapterRow is not null;
            bool? msiSupported = null;
            bool? idlePowerEnabled = null;
            if (showMsiCheck && boundAdapterRow is not null)
            {
                var msi = ReadRegistryDword(
                    $@"SYSTEM\CurrentControlSet\Enum\{boundAdapterRow.PnpDeviceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties",
                    "MSISupported");
                msiSupported = msi is null ? null : msi != 0;
                var idle = ReadRegistryDword(
                    $@"SYSTEM\CurrentControlSet\Enum\{boundAdapterRow.PnpDeviceId}\Device Parameters\StorPortIdlePowerSettings",
                    "IdlePowerEnable");
                idlePowerEnabled = idle is null ? null : idle != 0;
            }

            // #383: inventory - the adapter/disk rows already read above, plus fixed filter-driver
            // service names that (mostly) have no PnP device node of their own.
            var drivers = new List<StorageDriverInventoryEntry>();
            var seenServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in adapters)
            {
                string svc = row.ServiceName ?? row.DeviceName;
                if (!seenServices.Add(svc)) continue;
                drivers.Add(new StorageDriverInventoryEntry
                {
                    ServiceName = row.ServiceName ?? "Unknown",
                    DeviceName = row.DeviceName,
                    Version = row.Version,
                    DateText = row.DateText,
                    Signer = row.Signer,
                    SourceNote = "Windows driver inventory (Win32_PnPSignedDriver)",
                });
            }
            // Always include the vendor RAID/RST or storahci driver actually bound, even if it
            // wasn't already picked up above (it will have been, but this keeps the two passes
            // independent rather than relying on that).
            foreach (var name in AdditionalInventoryServiceNames)
            {
                if (!seenServices.Add(name)) continue;
                drivers.Add(ReadFileBasedDriverEntry(name));
            }

            var problemDevices = ReadProblemDevices();
            int? timeoutValue = ReadRegistryDword(@"SYSTEM\CurrentControlSet\Services\Disk", "TimeOutValue");

            return new StorageControllerFacts
            {
                Mode = mode,
                ModeDetailText = modeDetail,
                BoundDriverServiceName = boundService,
                ShowStorAhciMsiCheck = showMsiCheck,
                MsiSupported = msiSupported,
                IdlePowerEnabled = idlePowerEnabled,
                Drivers = drivers,
                ProblemDevices = problemDevices,
                DiskTimeoutValueSeconds = timeoutValue,
            };
        }
        catch (Exception ex)
        {
            return new StorageControllerFacts { Available = false, UnavailableReason = $"Could not read controller facts: {ex.Message}" };
        }
    }

    /// <summary>#383: a fixed-name storage filter/class driver (storport, disk, partmgr, volsnap,
    /// stornvme) that typically has no PnP device node of its own to look up in
    /// Win32_PnPSignedDriver - resolved instead from its Services\&lt;name&gt;\ImagePath straight to
    /// the .sys file on disk (FileVersionInfo for version, the file's own last-write time as a
    /// "date" - a proxy for the real WHQL submission date, captioned as such - and a best-effort
    /// Authenticode signer, same embedded-signature-only tradeoff SignatureCheckService already
    /// documents elsewhere in this app). Every field degrades independently to "Unknown" rather than
    /// dropping the whole row when the driver isn't present on this system (e.g. stornvme on a
    /// system with no NVMe controller).</summary>
    private static StorageDriverInventoryEntry ReadFileBasedDriverEntry(string serviceName)
    {
        string version = "Unknown";
        string dateText = "Unknown";
        string signer = "Unknown";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            string? imagePath = key?.GetValue("ImagePath") as string;
            if (imagePath is not null)
            {
                string resolved = ResolveDriverPath(imagePath);
                if (File.Exists(resolved))
                {
                    try { version = System.Diagnostics.FileVersionInfo.GetVersionInfo(resolved).FileVersion ?? "Unknown"; } catch { /* leave Unknown */ }
                    try { dateText = File.GetLastWriteTime(resolved).ToString("d") + " (file timestamp)"; } catch { /* leave Unknown */ }
                    try
                    {
                        using var cert = X509Certificate.CreateFromSignedFile(resolved);
                        using var cert2 = new X509Certificate2(cert);
                        string name = cert2.GetNameInfo(X509NameType.SimpleName, false);
                        signer = name.Length > 0 ? name : "Signed (signer name unavailable)";
                    }
                    catch { signer = "Unsigned/Unknown"; }
                }
            }
        }
        catch { /* Services key unreadable - this driver simply isn't present, leave everything Unknown */ }

        return new StorageDriverInventoryEntry
        {
            ServiceName = serviceName,
            DeviceName = serviceName,
            Version = version,
            DateText = dateText,
            Signer = signer,
            SourceNote = version == "Unknown" && dateText == "Unknown"
                ? "Not present on this system"
                : "Driver file on disk (no separate PnP device node for this driver)",
        };
    }

    private static string ResolveDriverPath(string imagePath)
    {
        string expanded = Environment.ExpandEnvironmentVariables(imagePath).Trim('"');
        if (expanded.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), expanded[@"\SystemRoot\".Length..]);
        else if (expanded.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase))
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), expanded);
        return expanded;
    }

    /// <summary>#383: storage-class PnP devices reporting a non-zero ConfigManagerErrorCode -
    /// informational ("worth a look"), never a claim about what's causing it.</summary>
    private static List<string> ReadProblemDevices()
    {
        var result = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity " +
                "WHERE (PNPClass = 'SCSIAdapter' OR PNPClass = 'HDC' OR PNPClass = 'DiskDrive') AND ConfigManagerErrorCode <> 0");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = (mo["Name"] as string ?? "Unknown device").Trim();
                int code = mo["ConfigManagerErrorCode"] is null ? 0 : Convert.ToInt32(mo["ConfigManagerErrorCode"]);
                result.Add($"{name}: code {code} ({DescribeConfigManagerErrorCode(code)})");
            }
        }
        catch { /* namespace unavailable - empty list */ }
        return result;
    }

    private static string DescribeConfigManagerErrorCode(int code) => code switch
    {
        1 => "device not configured correctly",
        10 => "device cannot start",
        18 => "reinstall drivers needed",
        19 => "registry may be corrupted",
        22 => "device disabled",
        24 => "device not present, not working, or missing drivers",
        28 => "drivers not installed",
        31 => "device not working properly - couldn't finish loading drivers",
        39 => "driver may be corrupted, or missing",
        43 => "device stopped because it reported problems",
        _ => "see Device Manager for details",
    };

    private static int? ReadRegistryDword(string subKeyPath, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKeyPath);
            return key?.GetValue(valueName) switch
            {
                int i => i,
                uint u => unchecked((int)u),
                _ => null,
            };
        }
        catch { return null; }
    }

    // ================================================================================
    // #382: per-disk write-cache / buffer-flushing / power-protection facts.
    // ================================================================================
    public static DiskWriteCacheInfo ReadWriteCacheInfo(int diskIndex)
    {
        string? pnpDeviceId = GetDiskPnpDeviceId(diskIndex);
        if (pnpDeviceId is null) return new DiskWriteCacheInfo { SummaryText = "Could not resolve this disk's device path." };

        string basePath = $@"SYSTEM\CurrentControlSet\Enum\{pnpDeviceId}\Device Parameters\Disk";
        int? cacheProtected = ReadRegistryDword(basePath, "CacheIsPowerProtected");
        int? userSetting = ReadRegistryDword(basePath, "UserWriteCacheSetting");

        bool cacheProtectedKnown = cacheProtected is not null;
        bool cacheProtectedValue = cacheProtected is > 0;
        bool userSettingKnown = userSetting is not null;
        int userSettingRaw = userSetting ?? 0;

        // Best-effort decode only - the exact enum this DWORD uses isn't published by Microsoft, so
        // this is presented as a labeled raw value rather than an assertion, per this app's
        // "degrade rather than fabricate" convention. 1 is treated (with reduced confidence) as
        // "write-cache buffer flushing disabled", matching the one publicly-observed convention for
        // this key - the risk flag below only fires on this specific, most-defensible reading.
        string settingText = userSettingKnown
            ? userSettingRaw switch
            {
                0 => "Not explicitly configured by the user (Windows/driver default applies).",
                1 => "Registry value 1 - on most drivers this means \"turn off Windows write-cache buffer flushing\" is enabled (flushing disabled).",
                2 => "Registry value 2 - on most drivers this means write caching is explicitly enabled with flushing left on.",
                _ => $"Registry value {userSettingRaw} (exact meaning is driver/Windows-version specific - not decoded here).",
            }
            : "Not explicitly configured by the user (Windows/driver default applies).";

        bool riskFlag = cacheProtectedKnown && !cacheProtectedValue && userSettingKnown && userSettingRaw == 1;

        string summary = cacheProtectedKnown
            ? (cacheProtectedValue
                ? "This drive's cache reports power-loss protection - a sudden power loss shouldn't lose data still sitting in the write cache."
                : "This drive's cache does NOT report power-loss protection.")
            : "This drive doesn't report whether its cache is power-loss protected (Unknown).";

        if (riskFlag)
            summary += " Combined with write-cache buffer flushing disabled (registry value 1 above), an unexpected power loss can lose recently-written data that was never physically committed to the media - this is a real data-loss risk, not just a quick flag.";
        else if (cacheProtectedKnown && !cacheProtectedValue)
            summary += " This alone is normal for most consumer drives; it only becomes a real risk if write-cache buffer flushing is also disabled (Device Manager > this disk > Properties > Policies).";

        return new DiskWriteCacheInfo
        {
            CacheIsPowerProtectedKnown = cacheProtectedKnown,
            CacheIsPowerProtected = cacheProtectedValue,
            UserWriteCacheSettingKnown = userSettingKnown,
            UserWriteCacheSettingRaw = userSettingRaw,
            UserWriteCacheSettingText = settingText,
            RiskFlag = riskFlag,
            SummaryText = summary,
        };
    }

    // ================================================================================
    // #384: firmware revision + best-effort slot/activation info, for the SMART details card's
    // drive header. The known-issue-list matching itself lives in FirmwareKnownIssueLookup (a pure
    // data table, no WMI) - this method only reads the live facts to match against.
    // ================================================================================

    /// <summary>MSFT_PhysicalDisk.FirmwareVersion for one disk, plus a best-effort call to
    /// GetFirmwareInformation (multi-slot firmware/activation info - mainly meaningful for NVMe
    /// drives with more than one firmware slot). Not every driver implements the method at all, so
    /// its failure is treated as "not available" rather than an error; the field-dump below is
    /// generic (no hardcoded output schema) precisely because that schema isn't documented with
    /// enough confidence to hardcode field names without risking a fabricated label.</summary>
    public static (string? Version, string SlotInfoText) ReadFirmwareInfo(int diskIndex)
    {
        string? version = null;
        string slotInfoText = "Not available (firmware slot/activation info isn't exposed by this drive/driver, or this drive doesn't support multiple firmware slots).";
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage", "SELECT DeviceId, FirmwareVersion FROM MSFT_PhysicalDisk");
            foreach (ManagementObject mo in searcher.Get())
            {
                if (mo["DeviceId"] is not string deviceIdStr || !int.TryParse(deviceIdStr, out int idx) || idx != diskIndex) continue;
                version = (mo["FirmwareVersion"] as string)?.Trim();

                try
                {
                    using var outParams = mo.InvokeMethod("GetFirmwareInformation", mo.GetMethodParameters("GetFirmwareInformation"), null);
                    if (outParams is not null)
                    {
                        var fields = new List<string>();
                        foreach (PropertyData p in outParams.Properties)
                        {
                            if (p.Value is null) continue;
                            if (p.Value is Array arr)
                                fields.Add($"{p.Name}: {arr.Length} entr{(arr.Length == 1 ? "y" : "ies")} reported");
                            else
                                fields.Add($"{p.Name}: {p.Value}");
                        }
                        if (fields.Count > 0) slotInfoText = string.Join(" · ", fields);
                    }
                }
                catch { /* method unsupported by this driver/drive - leave the default "not available" text */ }
                break;
            }
        }
        catch { /* namespace/class unavailable */ }
        return (version, slotInfoText);
    }
}
