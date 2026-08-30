using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TaskManagerPlus.Services;

/// <summary>One row of the ARP/neighbour cache (#532). <see cref="IsFlagged"/>/<see cref="FlagReason"/>
/// are set by <see cref="ArpCacheService.Flag"/> after the whole table is parsed - a mutable class
/// (not a record) so that pass can annotate rows by reference, the same shape RouteEntry (#513)
/// already uses for its own conflict flags.</summary>
public sealed class ArpEntry
{
    public string IpAddress { get; init; } = string.Empty;
    public string MacAddress { get; init; } = string.Empty; // "AA-BB-CC-DD-EE-FF", or "" for an incomplete entry
    public string InterfaceIp { get; init; } = string.Empty;
    public string InterfaceName { get; init; } = string.Empty; // NetworkInterface.Name, resolved from InterfaceIp - blank if it couldn't be matched
    public string EntryType { get; init; } = string.Empty; // "dynamic" / "static", as arp -a reports it
    public string Vendor { get; init; } = "Unknown vendor";
    public bool IsGateway { get; init; }
    public bool IsFlagged { get; set; }
    public string? FlagReason { get; set; }
}

/// <summary>#532's full read: the parsed table plus two convenience fields NetworkViewModel needs
/// for #533's gateway-MAC persistence without re-deriving them - the gateway's own resolved MAC (if
/// any), and whether the gateway has no ARP entry at all (as opposed to an incomplete one, which
/// shows up as a flagged row in <see cref="Entries"/> instead).</summary>
public sealed record ArpScanResult(List<ArpEntry> Entries, string? GatewayMac, bool GatewayEntryMissing);

/// <summary>
/// Item #532: the neighbour (ARP) cache as a sortable grid - IP / MAC / interface / entry type -
/// each MAC resolved to a vendor via a small embedded OUI-prefix table (this item's own "small
/// embedded table" spec, not a claim of completeness: just enough of the common consumer/networking
/// vendors to be useful on a typical home/office LAN; degrades to "Unknown vendor" for anything not
/// in it, never a guess, per CLAUDE.md's "degrade, never fabricate" convention).
///
/// Reads `arp -a` - the standard Windows tool, per this app's "known tool over raw interop"
/// convention, rather than P/Invoking GetIpNetTable2 - parsed per-interface section into a flat
/// list. On-demand (constructor call + explicit refresh), not a timer, matching the rest of this
/// tab's shell-out-based cards (#513 Routing, #511 interface MTU, ...).
///
/// This class's own two flags (two IPs sharing one MAC; an incomplete/missing gateway entry) are
/// computed in <see cref="Flag"/>. #533's gateway-MAC-*change* alert is a separate concern layered
/// on top by NetworkViewModel/GatewayFingerprintService once <see cref="ArpScanResult.GatewayMac"/>
/// is available, since that alert needs to persist state across refreshes rather than being a pure
/// function of one snapshot.
/// </summary>
public static class ArpCacheService
{
    public static async Task<ArpScanResult> ReadAsync(string? gatewayIp)
    {
        var entries = new List<ArpEntry>();
        var interfaceNames = BuildInterfaceIpToName();
        try
        {
            var psi = new ProcessStartInfo("arp.exe", "-a")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                // Start both stream reads before waiting (the shape every other shell-out here
                // uses): awaiting ReadToEndAsync first would block forever on a wedged arp.exe -
                // the timeout CTS wouldn't even exist yet, so the kill-on-timeout was unreachable
                // and the caller's IsRefreshingArp flag latched for the session. stderr is drained
                // too so a chatty error stream can't fill its pipe and wedge the process.
                var outputTask = proc.StandardOutput.ReadToEndAsync();
                var errorTask = proc.StandardError.ReadToEndAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await proc.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { try { proc.Kill(); } catch { /* best-effort */ } }

                string output = await outputTask;
                await errorTask;

                string currentInterfaceIp = string.Empty;
                foreach (var rawLine in output.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r').Trim();
                    if (line.Length == 0) continue;

                    if (line.StartsWith("Interface:", StringComparison.OrdinalIgnoreCase))
                    {
                        var header = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                        currentInterfaceIp = header.Length >= 2 ? header[1] : string.Empty;
                        continue;
                    }
                    if (line.StartsWith("Internet Address", StringComparison.OrdinalIgnoreCase)) continue;

                    var cols = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (cols.Length < 3) continue;
                    string ip = cols[0];
                    if (!IPAddress.TryParse(ip, out _)) continue;

                    bool incomplete = cols[1].Replace("-", string.Empty).Replace(":", string.Empty).TrimStart('0').Length == 0;
                    string mac = incomplete ? string.Empty : NormalizeMac(cols[1]);

                    entries.Add(new ArpEntry
                    {
                        IpAddress = ip,
                        MacAddress = mac,
                        InterfaceIp = currentInterfaceIp,
                        InterfaceName = interfaceNames.TryGetValue(currentInterfaceIp, out var name) ? name : string.Empty,
                        EntryType = cols[2],
                        Vendor = mac.Length == 0 ? "(incomplete)" : LookupVendor(mac),
                        IsGateway = gatewayIp is not null && ip.Equals(gatewayIp, StringComparison.OrdinalIgnoreCase),
                    });
                }
            }
        }
        catch
        {
            // Best-effort - return whatever was parsed before the failure.
        }

        Flag(entries);

        var gatewayRow = gatewayIp is null ? null : entries.FirstOrDefault(e => e.IsGateway && e.MacAddress.Length > 0);
        bool gatewayMissing = gatewayIp is not null && !entries.Any(e => e.IsGateway);
        return new ArpScanResult(entries, gatewayRow?.MacAddress, gatewayMissing);
    }

    /// <summary>#532's two conflict flags: more than one IP resolving to the same MAC (excluding
    /// broadcast/multicast, which legitimately have many "owners"), and an incomplete ARP entry for
    /// the gateway specifically - both "worth a manual check" flags, not proof of a problem, per
    /// CLAUDE.md's "quick flag, not a verdict" convention. Mutates the rows in place.</summary>
    private static void Flag(List<ArpEntry> entries)
    {
        foreach (var group in entries.Where(e => e.MacAddress.Length > 0 && !IsBroadcastOrMulticast(e.MacAddress)).GroupBy(e => e.MacAddress))
        {
            var rows = group.ToList();
            if (rows.Count <= 1) continue;
            foreach (var r in rows)
            {
                r.IsFlagged = true;
                r.FlagReason = $"{rows.Count} IP addresses resolve to this same MAC ({r.MacAddress}) - could be a NAT gateway/proxy-ARP setup serving multiple virtual IPs, or ARP spoofing. Quick flag, not a verdict.";
            }
        }

        var gatewayRow = entries.FirstOrDefault(e => e.IsGateway);
        if (gatewayRow is not null && gatewayRow.MacAddress.Length == 0)
        {
            gatewayRow.IsFlagged = true;
            gatewayRow.FlagReason = "This is the default gateway, but its ARP entry is incomplete (no MAC resolved) - ARP resolution to the gateway may be failing.";
        }
    }

    private static bool IsBroadcastOrMulticast(string mac) =>
        mac.Equals("FF-FF-FF-FF-FF-FF", StringComparison.OrdinalIgnoreCase) ||
        mac.StartsWith("01-00-5E", StringComparison.OrdinalIgnoreCase); // IPv4 multicast MAC range

    private static Dictionary<string, string> BuildInterfaceIpToName()
    {
        var map = new Dictionary<string, string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    map[addr.Address.ToString()] = ni.Name;
                }
            }
        }
        catch
        {
            // Best-effort - unresolved interface names just show blank.
        }
        return map;
    }

    private static string NormalizeMac(string raw) =>
        string.Join("-", raw.Split(new[] { '-', ':' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.PadLeft(2, '0').ToUpperInvariant()));

    /// <summary>OUI-prefix (first 3 octets) to vendor name - a small, hand-picked table of common
    /// consumer/networking/virtualization vendors, not the full IEEE registry. See this class's
    /// remarks for why an unmatched prefix degrades to "Unknown vendor" rather than a guess.</summary>
    private static readonly Dictionary<string, string> OuiVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["B8-27-EB"] = "Raspberry Pi Foundation",
        ["DC-A6-32"] = "Raspberry Pi Foundation",
        ["E4-5F-01"] = "Raspberry Pi Foundation",
        ["00-50-56"] = "VMware",
        ["00-0C-29"] = "VMware",
        ["00-1C-14"] = "VMware",
        ["08-00-27"] = "Oracle VirtualBox",
        ["0A-00-27"] = "Oracle VirtualBox",
        ["00-15-5D"] = "Microsoft (Hyper-V)",
        ["00-03-FF"] = "Microsoft",
        ["3C-D9-2B"] = "Hewlett Packard",
        ["94-57-A5"] = "Hewlett Packard",
        ["A0-D3-C1"] = "Hewlett Packard",
        ["A4-BB-6D"] = "Apple",
        ["AC-DE-48"] = "Apple",
        ["F0-18-98"] = "Apple",
        ["00-1B-63"] = "Apple",
        ["3C-15-C2"] = "Apple",
        ["28-F0-76"] = "Apple",
        ["00-17-88"] = "Signify/Philips (Hue)",
        ["EC-B5-FA"] = "Espressif (IoT/ESP)",
        ["24-6F-28"] = "Espressif (IoT/ESP)",
        ["3C-71-BF"] = "Espressif (IoT/ESP)",
        ["B0-4E-26"] = "TP-Link",
        ["50-C7-BF"] = "TP-Link",
        ["EC-08-6B"] = "TP-Link",
        ["F4-F2-6D"] = "TP-Link",
        ["A0-40-A0"] = "Netgear",
        ["44-94-FC"] = "Netgear",
        ["9C-3D-CF"] = "Netgear",
        ["1C-BD-B9"] = "Netgear",
        ["04-A1-51"] = "ASUSTek",
        ["1C-87-2C"] = "ASUSTek",
        ["AC-9E-17"] = "ASUSTek",
        ["38-D5-47"] = "ASUSTek",
        ["00-1D-D8"] = "Dell",
        ["D4-BE-D9"] = "Dell",
        ["B0-83-FE"] = "Dell",
        ["00-14-22"] = "Dell",
        ["00-16-B6"] = "Cisco-Linksys",
        ["00-1D-7E"] = "Cisco",
        ["00-21-D8"] = "Cisco",
        ["70-DF-2F"] = "Ubiquiti Networks",
        ["24-A4-3C"] = "Ubiquiti Networks",
        ["FC-EC-DA"] = "Ubiquiti Networks",
        ["78-8A-20"] = "Ubiquiti Networks",
        ["00-11-32"] = "Synology",
        ["00-24-32"] = "Netgear",
        ["18-A6-F7"] = "D-Link",
        ["1C-7E-E5"] = "D-Link",
        ["00-1E-58"] = "D-Link",
        ["B4-FB-E4"] = "D-Link",
        ["00-1A-11"] = "Google",
        ["3C-5A-B4"] = "Google",
        ["F4-F5-D8"] = "Google",
        ["44-07-0B"] = "Google (Nest)",
        ["18-B4-30"] = "Google (Nest)",
        ["F0-EF-86"] = "Amazon",
        ["68-37-E9"] = "Amazon",
        ["74-C2-46"] = "Amazon",
        ["00-1C-DF"] = "Amazon",
        ["50-DC-E7"] = "Amazon (Echo/Ring)",
        ["3C-6A-2C"] = "Samsung",
        ["8C-71-F8"] = "Samsung",
        ["E8-50-8B"] = "Samsung",
        ["00-26-37"] = "Nintendo",
        ["7C-BB-8A"] = "Nintendo",
        ["00-50-F2"] = "Microsoft",
    };

    public static string LookupVendor(string normalizedMac)
    {
        if (normalizedMac.Length < 8) return "Unknown vendor";
        string prefix = normalizedMac[..8]; // "AA-BB-CC"
        return OuiVendors.TryGetValue(prefix, out var vendor) ? vendor : "Unknown vendor";
    }
}
