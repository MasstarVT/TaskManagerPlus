using System.Diagnostics;
using System.Net.NetworkInformation;
using System.ServiceProcess;

namespace TaskManagerPlus.Services;

/// <summary>One orphaned virtual/VPN adapter (#578) - a candidate adapter for which no installed
/// service or staged driver package name could be matched. Name-based matching only, so this is a
/// quick flag, not a verdict - see <see cref="OrphanedAdapterService"/>'s remarks.</summary>
public sealed record OrphanedAdapterInfo(string AdapterName, string Description, string Reason);

/// <summary>
/// Item #578 (suggestions.md "Proxy, PAC, VPN and Winsock"): cross-references virtual/VPN/TAP
/// adapters present on the system against whether their owning service or driver package is still
/// installed - the leftover-TAP-adapter-still-holding-routes-and-binding-filters case a plain
/// adapter list can't distinguish from a normal, actively-used one.
///
/// Uses <see cref="NetworkInterface"/> (candidate adapters), <see cref="ServiceController"/> (the
/// managed equivalent of `sc query` this app's own ServiceControlService already uses elsewhere -
/// preferred over shelling out to sc.exe a second way to get the same information) and
/// `pnputil /enum-drivers` (per the item's own suggested source) for driver packages of any class -
/// deliberately not scoped to the Net device class like AdapterDriverStoreService's own #556 read,
/// since a VPN client's *own* installer package (as opposed to the virtual NIC's driver, which the
/// NIC itself would already show under Net) is commonly a different device class entirely.
///
/// Matching is necessarily best-effort: there's no exact join key between "an adapter's display
/// name" and "a service's name" or "a driver package's provider", so this compares significant
/// name tokens (generic words like "adapter"/"virtual"/"network" stripped out first) between the
/// adapter and every installed service/driver package. A false negative (a real owner exists but
/// wasn't matched by name) just means an adapter isn't flagged that maybe should be - the safer
/// failure direction for a "you might want to check this" flag.
/// </summary>
public static class OrphanedAdapterService
{
    // Mirrors NetworkDiagnosticsService.VpnNameHints (#37) on purpose - same "does this look like a
    // VPN/tunnel adapter" question - extended with a few more generic virtual-NIC name markers
    // (tap/tun/wintun) since this check specifically wants to catch the TAP/Wintun-style virtual NIC
    // a VPN client leaves behind, not just the client's own branded adapter name. Deliberately NOT
    // filtered to OperationalStatus == Up like that method - a leftover orphaned adapter is, if
    // anything, more likely to be disconnected than connected.
    private static readonly string[] VirtualAdapterHints =
    {
        "vpn", "wireguard", "tap-windows", "tap0", "tap9", "tun0", "wintun", "openvpn", "anyconnect",
        "nordvpn", "expressvpn", "zscaler", "globalprotect", "fortinet", "pritunl", "hamachi", "zerotier", "tailscale",
    };

    // Common always-legitimate virtual adapters that would otherwise false-positive on the hints
    // above or on NetworkInterfaceType alone - excluded outright, they're never "leftover from an
    // uninstalled client".
    private static readonly string[] ExcludeHints =
    {
        "hyper-v", "virtualbox host-only", "vmware", "wsl", "loopback", "npcap loopback", "bluetooth",
    };

    private static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "adapter", "virtual", "ethernet", "network", "driver", "miniport", "device", "windows",
        "for", "the", "and", "service", "connection", "interface",
    };

    public static async Task<List<OrphanedAdapterInfo>> FindOrphansAsync()
    {
        var results = new List<OrphanedAdapterInfo>();
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
                .Where(LooksLikeVirtualClientAdapter)
                .ToList();
            if (candidates.Count == 0) return results;

            var serviceTokenSets = ServiceController.GetServices()
                .Select(s =>
                {
                    HashSet<string> tokens;
                    try { tokens = Tokenize(s.ServiceName).Concat(Tokenize(s.DisplayName)).ToHashSet(StringComparer.OrdinalIgnoreCase); }
                    catch { tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
                    return tokens;
                })
                .ToList();

            var driverPackages = await ReadAllDriverPackagesAsync();
            var driverTokenSets = driverPackages
                .Select(p => Tokenize(p.ProviderName).Concat(Tokenize(p.OriginalName)).Concat(Tokenize(p.PublishedName)).ToHashSet(StringComparer.OrdinalIgnoreCase))
                .ToList();

            foreach (var ni in candidates)
            {
                var nameTokens = Tokenize(ni.Name).Concat(Tokenize(ni.Description)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (nameTokens.Count == 0) continue; // nothing distinctive enough to match against - don't flag on no information

                bool hasService = serviceTokenSets.Any(t => t.Overlaps(nameTokens));
                bool hasDriver = driverTokenSets.Any(t => t.Overlaps(nameTokens));
                if (hasService || hasDriver) continue;

                results.Add(new OrphanedAdapterInfo(ni.Name, ni.Description,
                    "No installed service or Driver Store package name matches this adapter - it may be left over from an uninstalled VPN/virtual-adapter client. Name-based matching only - quick flag, not a verdict."));
            }
        }
        catch
        {
            // Best-effort - a partial/empty result just means fewer orphans surfaced, never a crash.
        }
        return results;
    }

    private static bool LooksLikeVirtualClientAdapter(NetworkInterface ni)
    {
        string haystack = $"{ni.Name} {ni.Description}";
        if (ExcludeHints.Any(h => haystack.Contains(h, StringComparison.OrdinalIgnoreCase))) return false;

        bool looksVirtualType = ni.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel;
        bool looksByName = VirtualAdapterHints.Any(h => haystack.Contains(h, StringComparison.OrdinalIgnoreCase));
        return looksVirtualType || looksByName;
    }

    private static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = new(raw.Where(char.IsLetterOrDigit).ToArray());
            if (token.Length < 4) continue;
            if (GenericTokens.Contains(token)) continue;
            yield return token;
        }
    }

    private static async Task<List<(string ProviderName, string OriginalName, string PublishedName)>> ReadAllDriverPackagesAsync()
    {
        var packages = new List<(string, string, string)>();
        try
        {
            var (output, exitCode) = await ToolRunner.RunCapturedAsync("pnputil.exe", "/enum-drivers", 20000, timeoutOutput: string.Empty);
            if (exitCode is null) return packages;

            var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Flush()
            {
                if (block.Count == 0) return;
                packages.Add((
                    block.GetValueOrDefault("Provider Name", string.Empty),
                    block.GetValueOrDefault("Original Name", string.Empty),
                    block.GetValueOrDefault("Published Name", string.Empty)));
                block.Clear();
            }
            foreach (var rawLine in output.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) { Flush(); continue; }
                int idx = line.IndexOf(':');
                if (idx < 0) continue;
                string key = line[..idx].Trim();
                string value = line[(idx + 1)..].Trim();
                if (key.Length == 0) continue;
                block[key] = value;
            }
            Flush();
        }
        catch
        {
            // Best-effort.
        }
        return packages;
    }
}
