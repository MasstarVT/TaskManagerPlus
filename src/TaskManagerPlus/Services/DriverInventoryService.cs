using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #453: the Devices &amp; Drivers tab's primary inventory - `driverquery /v /fo csv` is the anchor
/// list (every currently-registered kernel driver, with its own State/Start Mode/Path columns),
/// enriched where a match exists with Win32_PnPSignedDriver metadata (Provider/INF/Version/Date/
/// Class) joined via the `HKLM\SYSTEM\CurrentControlSet\Enum\&lt;DeviceID&gt;\Service` registry
/// value (the same value Explorer/Device Manager use internally to tie a PnP device instance back
/// to its kernel service). Not every driverquery row has a PnP-signed counterpart - plenty of
/// OS-internal filter/bus/file-system drivers don't enumerate as a PnP device at all - those rows
/// simply keep "Unknown"/empty PnP-derived fields rather than a guessed value.
///
/// Also backs #458 (hardware-ID match quality) and #460 (kernel start type/load order), both plain
/// registry reads layered onto the same per-row data, and #459 (per-device installed-file list, via
/// Win32_PnPSignedDriverCIMDataFile/CIM_DataFile association traversal).
/// </summary>
public static class DriverInventoryService
{
    private sealed record DriverQueryRow(string DisplayName, string Description, string DriverType, string StartModeText, string State, string FilePath);
    private sealed record PnpInfo(string Provider, string InfName, string DriverVersion, DateTime? DriverDate, string DeviceClass, string PnpDeviceId);

    public static Task<List<DriverInventoryRow>> ListAsync() => Task.Run(async () =>
    {
        var driverQueryRows = await ReadDriverQueryAsync();
        var pnpByService = ReadPnpSignedDriversByService();

        var rows = new List<DriverInventoryRow>();
        foreach (var (serviceName, dq) in driverQueryRows)
        {
            pnpByService.TryGetValue(serviceName, out var pnp);

            var (regStart, regType, group, tag) = ReadServiceRegistryInfo(serviceName);
            string startTypeText = DescribeStartType(regStart, group, tag);

            string matchQuality = pnp?.PnpDeviceId is { } deviceId ? ComputeMatchQuality(deviceId) : "Unknown";

            string? companyName = ReadCompanyName(dq.FilePath);
            // #461: a driver whose publisher we couldn't identify at all is exactly the kind of
            // thing the third-party filter should surface, not silently hide alongside the known-
            // Microsoft rows it's meant to strip out.
            bool isThirdParty = string.IsNullOrEmpty(companyName) ||
                !companyName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

            rows.Add(new DriverInventoryRow
            {
                ServiceName = serviceName,
                DisplayName = dq.DisplayName.Length > 0 ? dq.DisplayName : serviceName,
                Description = dq.Description,
                DriverType = dq.DriverType,
                StartModeText = dq.StartModeText,
                State = dq.State,
                FilePath = dq.FilePath,
                Provider = pnp?.Provider ?? "Unknown",
                InfName = pnp?.InfName ?? string.Empty,
                DriverVersion = pnp?.DriverVersion ?? string.Empty,
                DriverDate = pnp?.DriverDate,
                DeviceClass = pnp?.DeviceClass ?? string.Empty,
                PnpDeviceId = pnp?.PnpDeviceId,
                RegistryStart = regStart,
                RegistryType = regType,
                Group = group,
                Tag = tag,
                StartTypeText = startTypeText,
                MatchQuality = matchQuality,
                CompanyName = companyName,
                IsThirdParty = isThirdParty,
            });
        }

        return rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    });

    /// <summary>#459: every file a driver package installed - Win32_PnPSignedDriverCIMDataFile is
    /// an association class linking one Win32_PnPSignedDriver to each Win32_CIMLogicalFile
    /// (.sys/.dll/.inf/...) it registered; ManagementObject.GetRelated walks that association
    /// without needing a hand-built WQL association-path string.</summary>
    public static Task<List<DriverFileInfo>> ListDriverFilesAsync(string? pnpDeviceId)
    {
        if (string.IsNullOrEmpty(pnpDeviceId)) return Task.FromResult(new List<DriverFileInfo>());
        return Task.Run(() => ListDriverFiles(pnpDeviceId));
    }

    private static List<DriverFileInfo> ListDriverFiles(string pnpDeviceId)
    {
        var files = new List<DriverFileInfo>();
        try
        {
            string escaped = pnpDeviceId.Replace("'", "''");
            using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_PnPSignedDriver WHERE DeviceID = '{escaped}'");
            foreach (ManagementObject driver in searcher.Get())
            {
                using (driver)
                {
                    foreach (ManagementBaseObject related in driver.GetRelated("CIM_DataFile"))
                    {
                        using var file = (ManagementObject)related;
                        string path = (file["Name"] as string ?? string.Empty).Trim();
                        if (path.Length == 0) continue;

                        long? size = null;
                        try { if (file["FileSize"] is { } fs) size = Convert.ToInt64(fs); } catch { /* leave null */ }

                        string? version = (file["Version"] as string)?.Trim();

                        files.Add(new DriverFileInfo
                        {
                            FileName = Path.GetFileName(path),
                            FilePath = path,
                            Version = string.IsNullOrEmpty(version) ? null : version,
                            SizeBytes = size,
                        });
                    }
                }
                break; // DeviceID is unique - only one Win32_PnPSignedDriver instance can match.
            }
        }
        catch
        {
            // Device removed since the inventory was loaded, WMI hiccup, ... - empty list.
        }
        return files.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<Dictionary<string, DriverQueryRow>> ReadDriverQueryAsync()
    {
        var result = new Dictionary<string, DriverQueryRow>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string output = await RunCapturedAsync("driverquery.exe", "/v /fo csv", timeoutMs: 25000);
            var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            if (lines.Count < 2) return result;

            var header = ParseCsvLine(lines[0]);
            int Idx(string name) => header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
            int iModule = Idx("Module Name"), iDisplay = Idx("Display Name"), iDesc = Idx("Description"),
                iType = Idx("Driver Type"), iStart = Idx("Start Mode"), iState = Idx("State"), iPath = Idx("Path");
            if (iModule < 0) return result;

            for (int i = 1; i < lines.Count; i++)
            {
                var fields = ParseCsvLine(lines[i]);
                if (fields.Count <= iModule) continue;
                string module = fields[iModule].Trim();
                if (module.Length == 0 || module.Equals("Module Name", StringComparison.OrdinalIgnoreCase)) continue;

                result[module] = new DriverQueryRow(
                    At(fields, iDisplay), At(fields, iDesc), At(fields, iType),
                    At(fields, iStart), At(fields, iState), At(fields, iPath));
            }
        }
        catch
        {
            // driverquery unavailable/failed - empty inventory, same degrade-to-nothing convention
            // as every other shell-out in this app.
        }
        return result;
    }

    /// <summary>Builds Win32_PnPSignedDriver metadata keyed by kernel service name (not DeviceID -
    /// that's what driverquery's own "Module Name" column gives us, so this is the join key that
    /// actually lines the two data sources up). The service name for a given PnP device instance
    /// isn't a WMI property at all; it only exists as the registry "Service" value under that
    /// device's Enum key, so this reads it there for every signed driver found.</summary>
    private static Dictionary<string, PnpInfo> ReadPnpSignedDriversByService()
    {
        var result = new Dictionary<string, PnpInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, DeviceName, DriverProviderName, Manufacturer, InfName, DriverVersion, DriverDate, DeviceClass FROM Win32_PnPSignedDriver");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    string? deviceId = mo["DeviceID"] as string;
                    if (string.IsNullOrEmpty(deviceId)) continue;

                    string? serviceName = ReadRegistryServiceNameForDevice(deviceId);
                    if (string.IsNullOrEmpty(serviceName)) continue;

                    string provider = (mo["DriverProviderName"] as string)?.Trim() is { Length: > 0 } dp
                        ? dp
                        : (mo["Manufacturer"] as string ?? string.Empty).Trim();

                    DateTime? driverDate = null;
                    if (mo["DriverDate"] is string wmiDate)
                    {
                        try { driverDate = ManagementDateTimeConverter.ToDateTime(wmiDate); } catch { /* leave null */ }
                    }

                    // Two device instances can legitimately share one kernel service (e.g. a pair
                    // of identical NICs both bound to the same driver) - first one found wins, they
                    // carry the same driver-package metadata either way.
                    result.TryAdd(serviceName, new PnpInfo(
                        provider.Length > 0 ? provider : "Unknown",
                        (mo["InfName"] as string ?? string.Empty).Trim(),
                        (mo["DriverVersion"] as string ?? string.Empty).Trim(),
                        driverDate,
                        (mo["DeviceClass"] as string ?? string.Empty).Trim(),
                        deviceId));
                }
            }
        }
        catch
        {
            // Namespace/class unavailable - every driverquery row just stays without a PnP-signed
            // counterpart (Provider/INF/Version/Date/Class all read "Unknown"/empty).
        }
        return result;
    }

    private static string? ReadRegistryServiceNameForDevice(string deviceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{deviceId}");
            return key?.GetValue("Service") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>#460: Start/Type/Group/Tag straight from the driver's own service key - the
    /// authoritative source for boot-start vs. system-start vs. demand-start and load-order group,
    /// independent of driverquery's own friendlier (but registry-value-identical) "Start Mode" text.</summary>
    private static (int? Start, int? Type, string? Group, int? Tag) ReadServiceRegistryInfo(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key is null) return (null, null, null, null);

            int? start = key.GetValue("Start") is int s ? s : null;
            int? type = key.GetValue("Type") is int t ? t : null;
            string? group = key.GetValue("Group") as string;
            int? tag = key.GetValue("Tag") is int tg ? tg : null;
            return (start, type, group, tag);
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    private static string DescribeStartType(int? start, string? group, int? tag)
    {
        if (start is null) return "Unknown";

        string startText = start switch
        {
            0 => "Boot-start",
            1 => "System-start",
            2 => "Auto-start",
            3 => "Demand-start",
            4 => "Disabled",
            _ => $"Start={start}",
        };

        if (string.IsNullOrWhiteSpace(group)) return startText;
        return tag is { } t ? $"{startText} (group: {group}, tag {t})" : $"{startText} (group: {group})";
    }

    /// <summary>
    /// #458: compares the device's own recorded hardware/compatible IDs against the MatchingDeviceId
    /// the driver package actually installed under for that device - the same field Device Manager's
    /// "Driver Details" reads internally to decide which INF entry matched. An exact hardware-ID
    /// match means the device is running its real, vendor-targeted driver; a compatible-ID-only
    /// match (or no match against either list) means it's running on a generic/basic fallback, e.g.
    /// "Microsoft Basic Display Adapter" standing in for a real GPU driver.
    /// </summary>
    private static string ComputeMatchQuality(string deviceId)
    {
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{deviceId}");
            if (enumKey is null) return "Unknown";

            string[] hardwareIds = enumKey.GetValue("HardwareID") as string[] ?? Array.Empty<string>();
            string[] compatibleIds = enumKey.GetValue("CompatibleIDs") as string[] ?? Array.Empty<string>();
            string? driverRef = enumKey.GetValue("Driver") as string; // "{classguid}\NNNN"
            if (string.IsNullOrEmpty(driverRef)) return "Unknown";

            using var classKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driverRef}");
            string? matchingDeviceId = classKey?.GetValue("MatchingDeviceId") as string;
            if (string.IsNullOrEmpty(matchingDeviceId)) return "Unknown";

            if (hardwareIds.Any(id => id.Equals(matchingDeviceId, StringComparison.OrdinalIgnoreCase))) return "Exact";
            if (compatibleIds.Any(id => id.Equals(matchingDeviceId, StringComparison.OrdinalIgnoreCase))) return "Compatible";
            return "Generic";
        }
        catch
        {
            return "Unknown";
        }
    }

    /// <summary>#461: file-version company name, the same signal Windows' own Device Manager
    /// "Driver Details" and Task Manager's process "Publisher" column read from. Null (not
    /// "Microsoft") when the file can't be read at all - ReadOnly/ListAsync's ThirdPartyOnly filter
    /// treats an unreadable/unknown publisher as third-party rather than silently hiding it.</summary>
    private static string? ReadCompanyName(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(filePath);
            string? company = info.CompanyName?.Trim();
            return string.IsNullOrEmpty(company) ? null : company;
        }
        catch
        {
            return null;
        }
    }

    private static string At(List<string> fields, int index) => index >= 0 && index < fields.Count ? fields[index].Trim() : string.Empty;

    /// <summary>Same concurrent-read/bounded-wait/kill-on-timeout shape as
    /// ScheduledTaskService.RunCapturedAsync - duplicated here rather than shared, matching how
    /// this app's other shell-out services each stay self-contained.</summary>
    private static async Task<string> RunCapturedAsync(string exe, string args, int timeoutMs = 10000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { /* best-effort */ }
            return string.Empty;
        }

        return (await outputTask) + (await errorTask);
    }

    // driverquery's CSV output quotes every field and escapes an embedded quote by doubling it ("") -
    // same hand-rolled parser as ScheduledTaskService/KernelModuleService, duplicated rather than
    // shared per this app's own established "each service stays self-contained" convention.
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}
