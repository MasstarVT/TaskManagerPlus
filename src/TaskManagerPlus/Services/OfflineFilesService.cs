using System.Management;
using System.ServiceProcess;

namespace TaskManagerPlus.Services;

/// <summary>One `Win32_OfflineFilesCache` entry - a cached share/folder tracked by Offline
/// Files (CSC). This WMI class's exact property set isn't consistently documented across Windows
/// versions, so rather than hardcode field names this app can't verify against a live
/// Offline-Files-enabled machine, every property the class actually reports is read generically
/// and kept as a plain key/value list - CLAUDE.md's "degrade to Unknown, never fabricate" applied
/// to a whole schema rather than one field.</summary>
public sealed class OfflineFilesCacheEntry
{
    public string Name { get; init; } = string.Empty;
    public List<(string Property, string Value)> Properties { get; init; } = new();

    // A handful of well-known property names (when present) surfaced directly, since they're the
    // ones actually worth a headline flag - anything else stays in Properties above for the
    // detail view.
    public bool? IsOnline { get; init; }
}

/// <summary>#590's overall state - the CscService's own running/enabled status, plus whatever
/// per-share cache entries WMI reported.</summary>
public sealed record OfflineFilesState(bool ServiceInstalled, bool ServiceRunning, bool FeatureEnabled, List<OfflineFilesCacheEntry> CacheEntries)
{
    /// <summary>#590: "hidden when Offline Files is not in use" - the card's own visibility gate.
    /// Not in use means the CSC service isn't even running AND WMI reports no cached shares at
    /// all, so there is nothing this card could show that isn't either "Unknown" or "off" - the
    /// same "hide the whole section rather than show an all-Unknown card" convention the Battery
    /// section and Storage Spaces card already follow.</summary>
    public bool IsInUse => ServiceRunning && CacheEntries.Count > 0;
}

/// <summary>
/// Item #590 (suggestions.md "SMB and network drives"): Offline Files (Client Side Caching/CSC)
/// lets a mapped share keep working, silently, from a local cache when the network path actually
/// drops - which means an edit can "save successfully" into the local cache while the share is
/// offline and never actually reach the server, with no visible error at the time. Reads the
/// CscService's own state via <see cref="ServiceController"/> (the same managed API
/// OrphanedAdapterService already prefers over shelling out to `sc query`) plus whatever
/// `Win32_OfflineFilesCache` reports for currently-tracked shares.
/// </summary>
public static class OfflineFilesService
{
    private const string ServiceName = "CscService";

    public static OfflineFilesState Read()
    {
        bool installed = false, running = false;
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status; // throws InvalidOperationException if the service doesn't exist on this machine
            installed = true;
            running = sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            // Service not installed (rare, but possible on a stripped-down build) - installed/running stay false.
        }

        bool featureEnabled = ReadFeatureEnabledFlag();

        var entries = new List<OfflineFilesCacheEntry>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", "SELECT * FROM Win32_OfflineFilesCache");
            foreach (ManagementObject mo in searcher.Get())
            {
                var props = new List<(string, string)>();
                bool? isOnline = null;
                string name = string.Empty;

                foreach (PropertyData p in mo.Properties)
                {
                    string valueText;
                    try { valueText = p.Value?.ToString() ?? string.Empty; }
                    catch { valueText = string.Empty; }

                    if (p.Name.Equals("Name", StringComparison.OrdinalIgnoreCase) && name.Length == 0)
                        name = valueText;

                    if ((p.Name.Contains("Online", StringComparison.OrdinalIgnoreCase) ||
                         p.Name.Contains("Status", StringComparison.OrdinalIgnoreCase)) &&
                        p.Value is bool b)
                    {
                        isOnline = b;
                    }

                    props.Add((p.Name, valueText));
                }

                if (name.Length == 0) name = "(unnamed cache entry)";
                entries.Add(new OfflineFilesCacheEntry { Name = name, Properties = props, IsOnline = isOnline });
            }
        }
        catch
        {
            // WMI class absent on this Windows version, or query failed outright - empty list,
            // same as every other "no data" case in this service.
        }

        return new OfflineFilesState(installed, running, featureEnabled, entries);
    }

    /// <summary>Whether the Offline Files feature itself is administratively enabled - read from
    /// the documented GPO-backed flag (`HKLM\SOFTWARE\Policies\Microsoft\Windows\NetCache!Enabled`,
    /// the same key the "Offline Files" Group Policy/Sync Center UI writes) rather than any
    /// service-start-type registry value, since a service can be Running while the feature itself
    /// is policy-disabled. An absent key means "no policy configured", read as enabled-by-default
    /// the way Windows itself treats an absent policy - the CscService's actual running state
    /// (above) is what ultimately reflects reality either way.</summary>
    private static bool ReadFeatureEnabledFlag()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\NetCache");
            if (key?.GetValue("Enabled") is int enabled) return enabled != 0;
        }
        catch
        {
            // Best-effort - fall through to the default below.
        }
        return true; // no explicit policy found - Windows' own default is "available", left to the service's actual running state to reflect reality
    }
}
