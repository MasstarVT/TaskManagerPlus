using System.Diagnostics;
using System.Net;
using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>One live IPv4/IPv6 route (#513). <see cref="IsFlagged"/>/<see cref="FlagReason"/> are
/// set by <see cref="RoutingTableService.FlagConflicts"/> after the whole table is parsed - a
/// mutable class (not a record) so that post-processing pass can annotate rows by reference
/// without the value-equality pitfalls of reconstructing records via `with`.</summary>
public sealed class RouteEntry
{
    public string AddressFamily { get; init; } = string.Empty; // "IPv4" or "IPv6"
    public string Destination { get; init; } = string.Empty;
    public string? Netmask { get; init; } // null for IPv6 - already expressed as a Destination prefix (e.g. "::/0")
    public string Gateway { get; init; } = string.Empty;
    public string InterfaceLabel { get; init; } = string.Empty; // IPv4: the local interface IP; IPv6: "if <index>"
    public int Metric { get; init; }
    public bool IsOnLink { get; init; }
    public bool IsDefaultRoute { get; init; }
    public bool IsFlagged { get; set; }
    public string? FlagReason { get; set; }
}

/// <summary>One persistent route (#514), read directly from the registry rather than `route
/// print`'s own Persistent Routes section, per the item's own spec. <see cref="Raw"/> is the
/// original comma-separated registry string, shown as a fallback when it doesn't parse into the
/// expected 4 fields - degrade to showing the raw value, never fabricate structure that isn't
/// there.</summary>
public sealed record PersistentRouteEntry(string AddressFamily, string Destination, string Netmask, string Gateway, string Metric, string Raw);

/// <summary>
/// Items #513/#514 (suggestions.md "Path MTU, routing and hop-level diagnostics"): a routing-table
/// viewer distinct from the per-adapter link-speed list already on this tab - this is "what path
/// does traffic actually take", the fastest way to see a VPN or virtual adapter silently hijacking
/// traffic (two default routes, a stale on-link route, a duplicate prefix).
///
/// The live table (#513) shells out to `route print` (the same "known Windows tool over raw
/// interop" tradeoff every other netsh/sc/schtasks call in this app already takes, rather than
/// P/Invoking GetIpForwardTable2) and is parsed into a flat, sortable list with each row's
/// conflict flags precomputed rather than left to a converter, so the DataGrid can just sort/filter
/// on IsFlagged directly.
///
/// The persistent table (#514) is read straight from
/// HKLM\SYSTEM\CurrentControlSet\Services\Tcpip[6]\Parameters\PersistentRoutes - routes `route -p`
/// wrote that survive a reboot, shown as a distinct section since a stale persistent route left by
/// an uninstalled VPN is a classic silent-breakage cause `route print`'s live table alone won't
/// explain (a route with no adapter to back it just silently fails to apply, rather than showing
/// up as visibly broken).
///
/// On-demand only, matching this tab's #510/#511 pair - `route print` is a subprocess shell-out,
/// not a trivial local read, so it's gated behind an explicit refresh rather than riding a timer.
/// </summary>
public static class RoutingTableService
{
    public static async Task<List<RouteEntry>> GetActiveRoutesAsync()
    {
        var routes = new List<RouteEntry>();
        routes.AddRange(await GetActiveRoutesForFamilyAsync("-4"));
        routes.AddRange(await GetActiveRoutesForFamilyAsync("-6"));
        FlagConflicts(routes);
        return routes;
    }

    private static async Task<List<RouteEntry>> GetActiveRoutesForFamilyAsync(string familyFlag)
    {
        var results = new List<RouteEntry>();
        try
        {
            var (output, _) = await ToolRunner.RunCapturedAsync("route.exe", $"print {familyFlag}", 10_000,
                timeoutOutput: string.Empty, includeStderr: false);

            bool inActiveSection = false;
            string af = familyFlag == "-4" ? "IPv4" : "IPv6";
            foreach (var rawLine in output.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Contains("Active Routes", StringComparison.Ordinal)) { inActiveSection = true; continue; }
                if (line.Contains("Persistent Routes", StringComparison.Ordinal)) { inActiveSection = false; continue; }
                if (!inActiveSection) continue;
                if (line.Contains("===", StringComparison.Ordinal)) continue;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Contains("Network Destination", StringComparison.Ordinal) || line.Contains("If Metric", StringComparison.Ordinal)) continue;
                if (line.Trim().Equals("None", StringComparison.OrdinalIgnoreCase)) continue;

                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

                if (af == "IPv4" && tokens.Length == 5)
                {
                    string dest = tokens[0], mask = tokens[1], gw = tokens[2], iface = tokens[3];
                    if (!int.TryParse(tokens[4], out int metric)) continue;
                    bool onLink = gw.Equals("On-link", StringComparison.OrdinalIgnoreCase);
                    bool isDefault = dest == "0.0.0.0" && mask == "0.0.0.0";
                    results.Add(new RouteEntry
                    {
                        AddressFamily = "IPv4", Destination = dest, Netmask = mask, Gateway = gw,
                        InterfaceLabel = iface, Metric = metric, IsOnLink = onLink, IsDefaultRoute = isDefault,
                    });
                }
                else if (af == "IPv6" && tokens.Length == 4)
                {
                    if (!int.TryParse(tokens[0], out int ifIndex)) continue;
                    if (!int.TryParse(tokens[1], out int metric)) continue;
                    string dest = tokens[2], gw = tokens[3];
                    bool onLink = gw.Equals("On-link", StringComparison.OrdinalIgnoreCase);
                    bool isDefault = dest.StartsWith("::/0", StringComparison.Ordinal);
                    results.Add(new RouteEntry
                    {
                        AddressFamily = "IPv6", Destination = dest, Netmask = null, Gateway = gw,
                        InterfaceLabel = $"if {ifIndex}", Metric = metric, IsOnLink = onLink, IsDefaultRoute = isDefault,
                    });
                }
            }
        }
        catch
        {
            // Best-effort - return whatever was parsed before the failure.
        }
        return results;
    }

    /// <summary>#513's three conflict flags: more than one default route, two routes to the same
    /// prefix at an equal metric, and an on-link route whose destination network doesn't actually
    /// contain its own interface address. All three are "worth a manual check" flags, not proof
    /// of a problem - see the class remarks and CLAUDE.md's "quick flag, not a verdict" convention.
    /// Mutates the rows in place.</summary>
    private static void FlagConflicts(List<RouteEntry> routes)
    {
        foreach (var afGroup in routes.Where(r => r.IsDefaultRoute).GroupBy(r => r.AddressFamily))
        {
            var rows = afGroup.ToList();
            if (rows.Count <= 1) continue;
            foreach (var r in rows)
            {
                r.IsFlagged = true;
                r.FlagReason = $"{rows.Count} {r.AddressFamily} default routes found - a VPN or virtual adapter may be redirecting traffic. Quick flag, not a verdict.";
            }
        }

        foreach (var group in routes.GroupBy(r => (r.AddressFamily, r.Destination, r.Netmask, r.Metric)))
        {
            var rows = group.ToList();
            if (rows.Count <= 1) continue;
            foreach (var r in rows)
            {
                if (r.IsFlagged) continue; // a duplicate-default row already carries the more specific reason above
                r.IsFlagged = true;
                r.FlagReason = "Duplicate route to the same destination at an equal metric - which one wins is ambiguous. Quick flag, not a verdict.";
            }
        }

        // On-link routes should contain their own interface's address - only checkable for IPv4,
        // where the Interface column is a literal local IP rather than an index.
        foreach (var r in routes.Where(r => r.AddressFamily == "IPv4" && r.IsOnLink && r.Netmask is not null))
        {
            if (r.IsFlagged) continue;
            if (IsSpecialIPv4Destination(r.Destination)) continue;
            if (NetworkContains(r.Destination, r.Netmask!, r.InterfaceLabel)) continue;

            r.IsFlagged = true;
            r.FlagReason = $"On-link route to {r.Destination}/{r.Netmask} doesn't contain this interface's own address ({r.InterfaceLabel}) - worth a manual check.";
        }
    }

    // Loopback, multicast, and the all-ones broadcast address are legitimately on-link without
    // "belonging" to any one interface's ordinary subnet - excluded to avoid flagging every normal
    // routing table.
    private static bool IsSpecialIPv4Destination(string destination) =>
        destination.StartsWith("127.", StringComparison.Ordinal) ||
        destination == "255.255.255.255" ||
        (IPAddress.TryParse(destination, out var ip) && ip.GetAddressBytes() is [>= 224, ..]);

    private static bool NetworkContains(string destination, string netmask, string ip)
    {
        if (!IPAddress.TryParse(destination, out var destIp) || !IPAddress.TryParse(netmask, out var maskIp) || !IPAddress.TryParse(ip, out var hostIp))
            return true; // can't parse one of the three - don't flag on incomplete information

        var destBytes = destIp.GetAddressBytes();
        var maskBytes = maskIp.GetAddressBytes();
        var hostBytes = hostIp.GetAddressBytes();
        if (destBytes.Length != maskBytes.Length || destBytes.Length != hostBytes.Length) return true;

        for (int i = 0; i < destBytes.Length; i++)
        {
            if ((hostBytes[i] & maskBytes[i]) != (destBytes[i] & maskBytes[i])) return false;
        }
        return true;
    }

    /// <summary>#514: routes that survive a reboot, read directly from the registry per the
    /// item's own spec - HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\PersistentRoutes
    /// for IPv4, the Tcpip6 equivalent for IPv6. Each REG_MULTI_SZ line is
    /// "&lt;destination&gt;,&lt;netmask&gt;,&lt;gateway&gt;,&lt;metric&gt;"; a line that doesn't
    /// split into exactly that shape is still shown (as <see cref="PersistentRouteEntry.Raw"/>)
    /// rather than dropped - degrade to showing the raw value, never fabricate structure.</summary>
    public static List<PersistentRouteEntry> ReadPersistentRoutes()
    {
        var results = new List<PersistentRouteEntry>();
        results.AddRange(ReadPersistentRoutesFor("IPv4", @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters"));
        results.AddRange(ReadPersistentRoutesFor("IPv6", @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters"));
        return results;
    }

    private static List<PersistentRouteEntry> ReadPersistentRoutesFor(string addressFamily, string keyPath)
    {
        var results = new List<PersistentRouteEntry>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key?.GetValue("PersistentRoutes") is string[] lines)
            {
                foreach (var line in lines)
                {
                    var parts = line.Split(',');
                    results.Add(parts.Length >= 4
                        ? new PersistentRouteEntry(addressFamily, parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), parts[3].Trim(), line)
                        : new PersistentRouteEntry(addressFamily, line, string.Empty, string.Empty, string.Empty, line));
                }
            }
        }
        catch
        {
            // Denied/absent key - degrade to "none found", not a crash.
        }
        return results;
    }
}
