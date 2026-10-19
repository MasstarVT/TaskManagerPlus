using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
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
/// Paired with a read of Windows' own duplicate-address-detection (DAD) result for the address -
/// the gratuitous-ARP check the OS runs when an address is assigned. There is no live re-probe:
/// SendARP for a local address is answered by the local stack with the machine's own MAC (nothing
/// goes on the wire), and no shell tool or managed API can send a raw ARP request, so a "probe"
/// built on it could only ever report "looks clear".
///
/// Both halves carry the standard heuristic caveat: a logged conflict event proves something
/// happened in the past, not that it's still true now; a clean DAD result covers the moment the
/// address was assigned, not a conflict that started later. Quick flag, not a verdict.
/// </summary>
public static class IpConflictService
{
    private static readonly Regex Ipv4Regex = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex MacRegex = new(@"\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.Compiled);

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

    /// <summary>#531's active check: Windows' own duplicate-address-detection state for
    /// <paramref name="ipAddress"/> (one of this machine's addresses) - see the class remarks for why
    /// this reads the OS's result instead of sending an ARP request itself.</summary>
    public static Task<(bool ConflictFound, string Message)> CheckDuplicateAddressStateAsync(string ipAddress) => Task.Run(() =>
    {
        if (!IPAddress.TryParse(ipAddress, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return (false, "Only IPv4 addresses can be checked this way.");

        DuplicateAddressDetectionState? state = null;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                var match = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.Equals(ip));
                if (match is null) continue;
                state = match.DuplicateAddressDetectionState;
                break;
            }
        }
        catch
        {
            return (false, "Couldn't read this address's duplicate-address-detection state.");
        }

        const string notLive = " This is the check Windows ran when the address was assigned, not a live probe - a conflict that started later shows up only in the event history above.";
        return state switch
        {
            null => (false, $"{ipAddress} isn't assigned to any adapter right now - refresh Addressing first."),
            DuplicateAddressDetectionState.Duplicate => (true, $"Windows' duplicate-address detection marked {ipAddress} as a duplicate - another host answered for this address when it was assigned. Likely a live IP conflict. Quick flag, not a verdict."),
            DuplicateAddressDetectionState.Preferred => (false, $"Windows' duplicate-address detection found no other host using {ipAddress} (state: Preferred).{notLive}"),
            DuplicateAddressDetectionState.Tentative => (false, $"Windows hasn't finished checking {ipAddress} yet (state: Tentative) - or the adapter has no link. Check again in a few seconds."),
            var other => (false, $"Windows reports {ipAddress} as {other} - it isn't in normal use on this adapter.{notLive}"),
        };
    });

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
