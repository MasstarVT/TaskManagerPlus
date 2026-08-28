using System.IO;
using System.Text.Json;

namespace TaskManagerPlus.Services;

/// <summary>One network profile's last-seen default-gateway MAC (#533).</summary>
public sealed class GatewayFingerprintEntry
{
    public string ProfileKey { get; set; } = string.Empty; // Wi-Fi SSID, or "Wired" for a wired connection - see NetworkViewModel's remarks
    public string GatewayIp { get; set; } = string.Empty;
    public string GatewayMac { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
}

public sealed class GatewayFingerprintFile
{
    public List<GatewayFingerprintEntry> Entries { get; set; } = new();
}

/// <summary>
/// Item #533: persists the default gateway's MAC address per network profile to
/// gateway-fingerprint.json under AppPaths.SettingsDirectory, and flags when it changes on what's
/// otherwise the same profile - either the router was replaced, or (a deliberately hedged quick
/// flag, per this item's own spec) something on the LAN is ARP-spoofing the gateway's address. Same
/// fail-silent-to-defaults JSON pattern as theme.json/ThemeService.
///
/// "Network profile" here is approximated as the current Wi-Fi SSID (from the existing #23 Wi-Fi
/// card's own WifiDiagnosticsService read) when on Wi-Fi, or the fixed key "Wired" otherwise - this
/// app has no simpler access to Windows' own richer NetworkListManager profile GUID without a COM
/// interop surface this feature doesn't otherwise need, so a machine with more than one wired
/// network (e.g. a laptop plugged into different docks) will share one "Wired" bucket rather than
/// getting a bucket per physical network. A known simplification, not a claim of exact profile
/// identity - the alert itself is explicitly "quick flag, not a verdict" regardless.
/// </summary>
public static class GatewayFingerprintService
{
    private static string SettingsPath => AppPaths.GetPath("gateway-fingerprint.json");

    public static GatewayFingerprintFile Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var file = JsonSerializer.Deserialize<GatewayFingerprintFile>(json);
                if (file is not null) return file;
            }
        }
        catch
        {
            // Corrupt/unreadable settings file - fall back to defaults.
        }
        return new GatewayFingerprintFile();
    }

    public static void Save(GatewayFingerprintFile file)
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

    /// <summary>Compares the currently-observed gateway MAC against the last one recorded for this
    /// profile, returns a "quick flag, not a verdict" message on a genuine change, and always
    /// updates the stored entry to the current observation (first sighting, or accepting the new MAC
    /// as the baseline going forward so the alert doesn't repeat every refresh after the first
    /// flag). Mutates <paramref name="file"/> in place; caller is responsible for Save()-ing it.</summary>
    public static string? CheckAndUpdate(GatewayFingerprintFile file, string profileKey, string gatewayIp, string gatewayMac)
    {
        var entry = file.Entries.FirstOrDefault(e => e.ProfileKey.Equals(profileKey, StringComparison.OrdinalIgnoreCase));
        string? message = null;
        if (entry is null)
        {
            file.Entries.Add(new GatewayFingerprintEntry
            {
                ProfileKey = profileKey, GatewayIp = gatewayIp, GatewayMac = gatewayMac, LastSeenUtc = DateTime.UtcNow,
            });
        }
        else
        {
            if (!entry.GatewayMac.Equals(gatewayMac, StringComparison.OrdinalIgnoreCase))
            {
                message = $"The gateway's MAC on \"{profileKey}\" changed from {entry.GatewayMac} to {gatewayMac} - could be a router replacement, or (a deliberately hedged quick flag) ARP spoofing on this network. Quick flag, not a verdict.";
            }
            entry.GatewayMac = gatewayMac;
            entry.GatewayIp = gatewayIp;
            entry.LastSeenUtc = DateTime.UtcNow;
        }
        return message;
    }
}
