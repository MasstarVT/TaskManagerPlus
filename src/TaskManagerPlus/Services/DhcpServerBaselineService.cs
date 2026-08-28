using System.IO;
using System.Text.Json;

namespace TaskManagerPlus.Services;

/// <summary>One physical adapter's last-observed DHCP server + leased subnet (#527/#534), keyed by
/// MAC address rather than adapter name/index so the baseline survives a friendly-name rename or a
/// driver reinstall that shuffles interface indices.</summary>
public sealed class DhcpServerBaselineEntry
{
    public string AdapterMac { get; set; } = string.Empty; // normalized, no separators, uppercase
    public string DhcpServer { get; set; } = string.Empty;
    public string LeasedIp { get; set; } = string.Empty;
    public string SubnetMask { get; set; } = string.Empty;
    public DateTime LastUpdatedUtc { get; set; }
}

public sealed class DhcpServerBaselineFile
{
    public List<DhcpServerBaselineEntry> Entries { get; set; } = new();
}

/// <summary>
/// Items #527 (rogue-DHCP-server flag) and #534 (static-IP-vs-last-known-DHCP-range sanity check):
/// persists the DHCP server address and leased subnet last seen on each physical adapter to
/// dhcp-server-baseline.json under AppPaths.SettingsDirectory - same fail-silent-to-defaults JSON
/// pattern as theme.json/ThemeService and #504's latency-baseline.json (LatencyBaselineService), a
/// missing/corrupt file just means "no baseline recorded yet", not a crash.
///
/// One file backs both items because they're the same underlying fact observed twice: #527 asks
/// "did the server answering this lease change", #534 asks "does this now-static address still
/// belong on the network DHCP was last handing addresses out on" - both need "what did DHCP last
/// tell us about this adapter", so there's no reason to track it twice.
/// </summary>
public static class DhcpServerBaselineService
{
    private static string SettingsPath => AppPaths.GetPath("dhcp-server-baseline.json");

    public static DhcpServerBaselineFile Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var file = JsonSerializer.Deserialize<DhcpServerBaselineFile>(json);
                if (file is not null) return file;
            }
        }
        catch
        {
            // Corrupt/unreadable settings file - fall back to defaults.
        }
        return new DhcpServerBaselineFile();
    }

    public static void Save(DhcpServerBaselineFile file)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }

    /// <summary>#527: compares the currently-observed DHCP server against the last one recorded for
    /// this adapter, returns a "quick flag, not a verdict" message on a genuine change, and always
    /// updates the stored entry to the current observation (server + leased subnet) - so the next
    /// call reflects "now" (no repeated flag for the same server), and so #534's static-vs-DHCP-range
    /// check below has a subnet to compare a later static configuration against. Mutates
    /// <paramref name="file"/> in place; caller is responsible for Save()-ing it.</summary>
    public static string? CheckAndUpdate(DhcpServerBaselineFile file, string adapterMac, string dhcpServer, string leasedIp, string subnetMask)
    {
        var entry = file.Entries.FirstOrDefault(e => e.AdapterMac == adapterMac);
        string? message = null;
        if (entry is null)
        {
            file.Entries.Add(new DhcpServerBaselineEntry
            {
                AdapterMac = adapterMac, DhcpServer = dhcpServer, LeasedIp = leasedIp, SubnetMask = subnetMask, LastUpdatedUtc = DateTime.UtcNow,
            });
        }
        else
        {
            if (entry.DhcpServer.Length > 0 && !entry.DhcpServer.Equals(dhcpServer, StringComparison.OrdinalIgnoreCase))
            {
                message = $"This adapter was previously served by DHCP server {entry.DhcpServer}; it's now {dhcpServer}. Could be a legitimate router replacement or new DHCP scope, or an unauthorized (\"rogue\") DHCP server on this network. Quick flag, not a verdict.";
            }
            entry.DhcpServer = dhcpServer;
            entry.LeasedIp = leasedIp;
            entry.SubnetMask = subnetMask;
            entry.LastUpdatedUtc = DateTime.UtcNow;
        }
        return message;
    }

    /// <summary>#534: the last DHCP-served IP/subnet recorded for this adapter, if any - used to
    /// flag a *currently static* configuration that doesn't match what DHCP last handed out on the
    /// same physical adapter.</summary>
    public static (string LeasedIp, string SubnetMask)? GetLastKnownDhcpSubnet(DhcpServerBaselineFile file, string adapterMac)
    {
        var entry = file.Entries.FirstOrDefault(e => e.AdapterMac == adapterMac);
        return entry is null || entry.SubnetMask.Length == 0 ? null : (entry.LeasedIp, entry.SubnetMask);
    }
}
