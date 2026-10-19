using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>One qualifying failure/timeout event (#524).</summary>
public sealed record DnsFailureEvent(DateTime TimeUtc, string Source, string QueriedName, string? ResolverIp, string Summary);

/// <summary>One grouped bucket - by queried name or by resolver - for #524's results table.</summary>
public sealed record DnsFailureGroup(string Key, int Count, DateTime LastSeenUtc);

/// <summary>#524's full scan result. <see cref="SystemLogAvailable"/> is false when the System log
/// couldn't be queried for the DNS-Client timeout events at all (access denied, log service
/// unavailable) - distinct from a successful scan that simply found nothing.</summary>
public sealed record DnsFailureScanResult(
    List<DnsFailureEvent> Events, List<DnsFailureGroup> ByName, List<DnsFailureGroup> ByResolver, bool SystemLogAvailable);

/// <summary>
/// Item #524: scans the System log for Microsoft-Windows-DNS-Client resolution-timeout events (ID
/// 1014 is defined for the System channel only - the DNS-Client/Operational channel never carries
/// it, and ships disabled anyway), plus the System log's own Dnscache provider entries, over a
/// caller-chosen lookback window - grouped
/// by queried name and (best-effort) by resolver, so a resolver that's intermittently failing
/// shows up as a count instead of needing to be inferred from a wall of individual log lines.
///
/// Same shape EventLogService already establishes for the Stability tab's own event-log reads
/// (Level filter, ReverseDirection query, degrade-to-empty on any failure) - a fresh class rather
/// than extending EventLogService since this reads an entirely different channel with entirely
/// different event IDs, not more of what that class already covers. On-demand only, behind an
/// explicit "Scan" button (the CLAUDE.md on-demand-for-event-log-scans convention), never a timer.
///
/// The timeout event (ID 1014) carries the queried name in its QueryName field, and the resolver
/// as a raw SOCKADDR in its Address field (hex in the event XML) - decoded here, never guessed from
/// the message text (a PTR query's reversed-octet name would look like an IP to a text regex).
/// Where no decodable address is present, the event is grouped
/// under an honestly-labeled "(resolver not identified in event data)" bucket rather than a
/// fabricated one - CLAUDE.md's "degrade, never fabricate" convention applied to event-log
/// resolver attribution.
/// </summary>
public static class DnsEventLogService
{
    private const string DnsClientProvider = "Microsoft-Windows-DNS-Client";
    private const int TimeoutEventId = 1014; // "Name resolution for the name %1 timed out after none of the configured DNS servers responded"
    private const int MaxEvents = 2000;
    private const string UnidentifiedResolverLabel = "(resolver not identified in event data)";

    private static readonly Regex QueriedNameFromMessageRegex = new(@"name\s+([^\s]+)\s+timed out", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QueryNameDataRegex = new(@"<Data Name=['""]QueryName['""]>([^<]*)</Data>", RegexOptions.Compiled);
    private static readonly Regex AddressDataRegex = new(@"<Data Name=['""]Address['""]>([0-9A-Fa-f]+)</Data>", RegexOptions.Compiled);

    public static DnsFailureScanResult Scan(TimeSpan window)
    {
        var events = new List<DnsFailureEvent>();
        bool systemAvailable = true;
        long maxAgeMs = (long)Math.Max(1, window.TotalMilliseconds);

        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{DnsClientProvider}'] and EventID={TimeoutEventId} and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered locally - a known, common gap

                    string xml;
                    try { xml = record.ToXml(); }
                    catch { xml = string.Empty; }

                    var nameMatch = QueryNameDataRegex.Match(xml);
                    string queriedName = nameMatch.Success && nameMatch.Groups[1].Value.Trim().Length > 0
                        ? nameMatch.Groups[1].Value.Trim().TrimEnd('.')
                        : ExtractQueriedName(message);
                    events.Add(new DnsFailureEvent(
                        record.TimeCreated ?? DateTime.MinValue, "DNS-Client timeout", queriedName, DecodeResolverAddress(xml), Truncate(message, 200)));
                }
            }
        }
        catch
        {
            // Access denied or the event log service unavailable - degrade to empty.
            systemAvailable = false;
        }

        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Dnscache'] and (Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxSystemEvents = 500;
            while (count < maxSystemEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    string xml;
                    try { xml = record.ToXml(); }
                    catch { xml = string.Empty; }

                    events.Add(new DnsFailureEvent(
                        record.TimeCreated ?? DateTime.MinValue, "System log (Dnscache)", "(not name-specific)", DecodeResolverAddress(xml), Truncate(message, 200)));
                }
            }
        }
        catch
        {
            // Best-effort - the DNS-Client timeout results above still stand on their own.
        }

        var byName = events
            .Where(e => e.QueriedName != "(not name-specific)" && e.QueriedName != "(unknown)")
            .GroupBy(e => e.QueriedName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DnsFailureGroup(g.Key, g.Count(), g.Max(e => e.TimeUtc)))
            .OrderByDescending(g => g.Count)
            .ToList();

        var byResolver = events
            .GroupBy(e => e.ResolverIp ?? UnidentifiedResolverLabel, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DnsFailureGroup(g.Key, g.Count(), g.Max(e => e.TimeUtc)))
            .OrderByDescending(g => g.Count)
            .ToList();

        return new DnsFailureScanResult(
            events.OrderByDescending(e => e.TimeUtc).ToList(), byName, byResolver, systemAvailable);
    }

    private static string ExtractQueriedName(string message)
    {
        var match = QueriedNameFromMessageRegex.Match(message);
        return match.Success ? match.Groups[1].Value.TrimEnd('.') : "(unknown)";
    }

    /// <summary>Decodes the first SOCKADDR in the event's Address field (hex in the event XML):
    /// family 2 (AF_INET) carries the IPv4 address at bytes 4-7, family 23 (AF_INET6) the IPv6
    /// address at bytes 8-23. Anything else, or an all-zero address, is null - never guessed.</summary>
    private static string? DecodeResolverAddress(string xml)
    {
        try
        {
            var match = AddressDataRegex.Match(xml);
            if (!match.Success || match.Groups[1].Value.Length % 2 != 0) return null;
            byte[] bytes = Convert.FromHexString(match.Groups[1].Value);
            if (bytes.Length < 2) return null;

            int family = bytes[0] | (bytes[1] << 8); // little-endian sa_family
            IPAddress? address = family switch
            {
                2 when bytes.Length >= 8 => new IPAddress(bytes.AsSpan(4, 4)),
                23 when bytes.Length >= 24 => new IPAddress(bytes.AsSpan(8, 16)),
                _ => null,
            };
            if (address is null || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return null;
            return address.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
