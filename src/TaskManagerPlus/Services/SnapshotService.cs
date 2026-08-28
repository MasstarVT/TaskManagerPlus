using System.IO;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Captures and compares a point-in-time snapshot of installed software / services / startup
/// items (#93/#94). A single mechanism serves both suggestions: "baseline capture, later
/// comparison" and "what changed between two points in time" are the same capture-then-diff
/// operation, just framed by when you happen to run the comparison. Reads its own data
/// independently (registry Uninstall keys, ServiceController, StartupManagerService) rather than
/// reusing the Processes/Services/Startup tabs' already-loaded collections, so a snapshot capture
/// isn't affected by whatever filter/sort state those tabs currently have applied.
/// </summary>
public static class SnapshotService
{
    /// <summary>Round 12, #94: `idleCpuTempC` is optional and supplied by the caller (SummaryViewModel
    /// already knows the live EnergyThermals/Performance state at the moment "Save snapshot" is
    /// clicked; this static service has no sensor access of its own) - see SystemSnapshot.IdleCpuTempC's
    /// remarks for the idle-gating this relies on the caller to have already applied.
    ///
    /// #486: also gathers driver inventory (#453) and driver store contents (#479) - genuinely
    /// slower than every other field here (driverquery.exe plus pnputil /enum-drivers, both a few
    /// seconds), which is why this is CaptureAsync rather than the plain synchronous method it used
    /// to be; SummaryViewModel's SaveSnapshotCommand/CompareSnapshotCommand are AsyncRelayCommand
    /// for exactly this reason. Still a single explicit user action (Save/Compare snapshot), so
    /// this is squarely within CLAUDE.md's on-demand-action convention rather than a timer.</summary>
    public static async Task<SystemSnapshot> CaptureAsync(double? idleCpuTempC = null)
    {
        var driverInventory = await DriverInventoryService.ListAsync();
        var driverStore = await DriverStoreService.ListAsync();

        return new SystemSnapshot
        {
            CapturedAt = DateTime.Now,
            InstalledSoftware = ReadInstalledSoftwareNames(),
            Services = ReadServiceNames(),
            StartupItems = new StartupManagerService().Sample().Select(i => i.Name).ToList(),
            // Round 7 #16: StartType/logon-account config per service, alongside the plain name
            // list above - lets the Services tab's config-drift check reuse this same baseline
            // file instead of inventing a second one.
            ServiceConfigs = ServiceControlService.ReadServiceConfigs(),
            IdleCpuTempC = idleCpuTempC,
            DriverInventory = driverInventory
                .Select(d => $"{d.ServiceName} — {(string.IsNullOrEmpty(d.InfName) ? "no INF" : d.InfName)} {d.DriverVersion}".TrimEnd())
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            DriverStorePackages = driverStore.Packages
                .Select(p => $"{p.PublishedName} — {p.OriginalName} ({p.Provider}) {p.DriverVersionText}".TrimEnd())
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private static List<string> ReadInstalledSoftwareNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                        if (sub.GetValue("SystemComponent") is int sc && sc == 1) continue;

                        names.Add(name);
                    }
                    catch { /* one malformed subkey shouldn't stop the rest of the scan */ }
                }
            }
            catch { /* registry hive/path unavailable */ }
        }
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ReadServiceNames()
    {
        try
        {
            return ServiceController.GetServices()
                .Select(s => s.ServiceName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void Save(SystemSnapshot snapshot, string path)
    {
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public static SystemSnapshot? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SystemSnapshot>(json);
        }
        catch
        {
            return null;
        }
    }

    public static SnapshotDiff Diff(SystemSnapshot baseline, SystemSnapshot current)
    {
        (List<string> Added, List<string> Removed) DiffSet(List<string> before, List<string> after)
        {
            var beforeSet = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase);
            var afterSet = new HashSet<string>(after, StringComparer.OrdinalIgnoreCase);
            return (
                after.Where(a => !beforeSet.Contains(a)).OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList(),
                before.Where(b => !afterSet.Contains(b)).OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList());
        }

        var (softwareAdded, softwareRemoved) = DiffSet(baseline.InstalledSoftware, current.InstalledSoftware);
        var (servicesAdded, servicesRemoved) = DiffSet(baseline.Services, current.Services);
        var (startupAdded, startupRemoved) = DiffSet(baseline.StartupItems, current.StartupItems);
        // #486: same DiffSet shape for driver inventory/driver store - both lists just default to
        // empty on a snapshot file saved before this field existed, so diffing against one shows
        // everything in the newer snapshot as "added" rather than throwing on a missing field.
        var (driversAdded, driversRemoved) = DiffSet(baseline.DriverInventory, current.DriverInventory);
        var (storeAdded, storeRemoved) = DiffSet(baseline.DriverStorePackages, current.DriverStorePackages);

        return new SnapshotDiff
        {
            BaselineCapturedAt = baseline.CapturedAt,
            SoftwareAdded = softwareAdded,
            SoftwareRemoved = softwareRemoved,
            ServicesAdded = servicesAdded,
            ServicesRemoved = servicesRemoved,
            StartupAdded = startupAdded,
            StartupRemoved = startupRemoved,
            DriversAdded = driversAdded,
            DriversRemoved = driversRemoved,
            DriverStorePackagesAdded = storeAdded,
            DriverStorePackagesRemoved = storeRemoved,
        };
    }
}
