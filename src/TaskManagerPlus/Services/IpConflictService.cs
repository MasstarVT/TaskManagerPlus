using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>One System-log Tcpip address-conflict event (#531).</summary>
public sealed record IpConflictLogEntry(DateTime TimeUtc, string? ConflictingIp, string? ConflictingMac, bool MatchesCurrentAddress, string Message);

public sealed record IpConflictScanResult(List<IpConflictLogEntry> Events, bool ChannelAvailable);

/// <summary>
/// Item #531: correlates System-log Tcpip event IDs 4198/4199 ("The system detected an address
/// conflict...") with the machine's current adapter addresses, surfacing the conflicting MAC when
/// the event text carries it - a passive, historical view.
///
/// Paired with an *active* check: a gratuitous-ARP-style probe of the machine's own address via the
/// documented IP Helper <c>SendARP</c> function. There's no shell tool for originating a live ARP
/// request on demand (arp.exe only manages the local cache; it can't send a packet), so this is one
/// of the few places in this app that P/Invokes directly - per CLAUDE.md's convention, reserved for
/// exactly this "no tool or WMI class available at all" case, and run on an abandoned background
/// thread with a strict timeout the same defensive way HandleInspectionService's NtQueryObject calls
/// are, since SendARP can block if the local network stack is in an odd state.
///
/// Both halves carry the standard heuristic caveat: a logged conflict event proves something
/// happened in the past, not that it's still true now; a clean probe proves nothing answered in the
/// few seconds it ran, not that nothing ever will. Quick flag, not a verdict.
/// </summary>
public static class IpConflictService
{
    private static readonly Regex Ipv4Regex = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex MacRegex = new(@"\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.Compiled);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public static IpConflictScanResult ScanSystemLog(TimeSpan window, IReadOnlyCollection<string> currentIpAddresses)
    {
        var events = new List<IpConflictLogEntry>();
        bool available = true;
        try
        {
            long maxAgeMs = (long)Math.Max(1, window.TotalMilliseconds);
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Tcpip'] and (EventID=4198 or EventID=4199) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 500;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    var ipMatch = Ipv4Regex.Match(message);
                    string? ip = ipMatch.Success ? ipMatch.Value : null;
                    var macMatch = MacRegex.Match(message);
                    string? mac = macMatch.Success ? macMatch.Value.Replace(':', '-').ToUpperInvariant() : null;
                    bool matches = ip is not null && currentIpAddresses.Contains(ip, StringComparer.OrdinalIgnoreCase);

                    events.Add(new IpConflictLogEntry(record.TimeCreated ?? DateTime.MinValue, ip, mac, matches, Truncate(message, 260)));
                }
            }
        }
        catch
        {
            // Channel/provider unavailable, access denied, or no matching events - degrade to empty.
            available = false;
        }
        return new IpConflictScanResult(events.OrderByDescending(e => e.TimeUtc).ToList(), available);
    }

    /// <summary>#531's active check: issues a live ARP request for <paramref name="ipAddress"/> (this
    /// machine's own address) and reports whether some *other* MAC answered for it - the same
    /// address-conflict-detection technique (RFC 5227) an OS runs before binding a DHCP lease.
    /// Bounded by <see cref="ProbeTimeout"/> on an abandoned background thread - see the class
    /// remarks for why SendARP isn't trusted to always return promptly.</summary>
    public static async Task<(bool ConflictFound, string Message)> ProbeOwnAddressAsync(string ipAddress, string? ownMacAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return (false, "Only IPv4 addresses can be probed this way.");

        var (found, mac) = await Task.Run(() => SendArpProbeWithTimeout(ip));
        if (!found) return (false, "No other host answered for this address on the wire - looks clear right now.");

        bool isSelf = !string.IsNullOrEmpty(ownMacAddress) && mac.Equals(ownMacAddress.Replace(':', '-'), StringComparison.OrdinalIgnoreCase);
        return isSelf
            ? (false, $"Only this machine ({mac}) answered - looks clear right now.")
            : (true, $"Another host at {mac} answered for this machine's own address ({ipAddress}) - a live IP conflict. Quick flag, not a verdict.");
    }

    private static (bool Found, string Mac) SendArpProbeWithTimeout(IPAddress ip)
    {
        (bool Found, string Mac) result = (false, string.Empty);
        var worker = new Thread(() =>
        {
            try
            {
                uint destIp = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
                var macBytes = new byte[6];
                uint macLen = (uint)macBytes.Length;
                int status = SendARP(destIp, 0, macBytes, ref macLen);
                if (status == 0 && macLen > 0)
                {
                    string mac = string.Join("-", macBytes.Take((int)macLen).Select(b => b.ToString("X2")));
                    result = (true, mac);
                }
            }
            catch
            {
                // leave result as "not found"
            }
        })
        { IsBackground = true };

        worker.Start();
        worker.Join(ProbeTimeout); // never joined past the timeout - a hung call is simply abandoned
        return result;
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physAddrLen);
}
