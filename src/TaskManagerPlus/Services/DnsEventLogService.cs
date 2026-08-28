using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>One qualifying failure/timeout event (#524).</summary>
public sealed record DnsFailureEvent(DateTime TimeUtc, string Source, string QueriedName, string? ResolverIp, string Summary);

/// <summary>One grouped bucket - by queried name or by resolver - for #524's results table.</summary>
public sealed record DnsFailureGroup(string Key, int Count, DateTime LastSeenUtc);

/// <summary>#524's full scan result. <see cref="OperationalChannelAvailable"/> is false when the
/// Microsoft-Windows-DNS-Client/Operational channel couldn't be read at all - it ships disabled by
/// default on a stock Windows install (an administrator has to turn it on via Event Viewer's "Show
/// Analytic and Debug Logs" or `wevtutil sl ... /e:true`), which is the single most common reason
/// this scan comes back empty, not a bug.</summary>
public sealed record DnsFailureScanResult(
    List<DnsFailureEvent> Events, List<DnsFailureGroup> ByName, List<DnsFailureGroup> ByResolver, bool OperationalChannelAvailable);

/// <summary>
/// Item #524: scans Microsoft-Windows-DNS-Client/Operational for resolution-timeout events, plus
/// the System log's own Dnscache provider entries, over a caller-chosen lookback window - grouped
/// by queried name and (best-effort) by resolver, so a resolver that's intermittently failing
/// shows up as a count instead of needing to be inferred from a wall of individual log lines.
///
/// Same shape EventLogService already establishes for the Stability tab's own event-log reads
/// (Level filter, ReverseDirection query, degrade-to-empty on any failure) - a fresh class rather
/// than extending EventLogService since this reads an entirely different channel with entirely
/// different event IDs, not more of what that class already covers. On-demand only, behind an
/// explicit "Scan" button (the CLAUDE.md on-demand-for-event-log-scans convention), never a timer.
///
/// The DNS-Client Operational log's own timeout event (ID 1014) reports the queried name reliably
/// but its formatted message doesn't reliably name the specific resolver that failed to answer -
/// this app makes no attempt to guess one from the message text. Where a resolver's IP genuinely
/// appears in the raw event's own XML payload, it's used as-is; otherwise the event is grouped
/// under an honestly-labeled "(resolver not identified in event data)" bucket rather than a
/// fabricated one - CLAUDE.md's "degrade, never fabricate" convention applied to event-log
/// resolver attribution.
/// </summary>
public static class DnsEventLogService
{
    private const string OperationalChannel = "Microsoft-Windows-DNS-Client/Operational";
    private const int TimeoutEventId = 1014; // "Name resolution for the name %1 timed out after none of the configured DNS servers responded"
    private const int MaxEvents = 2000;
    private const string UnidentifiedResolverLabel = "(resolver not identified in event data)";

    private static readonly Regex QueriedNameFromMessageRegex = new(@"name\s+([^\s]+)\s+timed out", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Ipv4Regex = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);

    public static DnsFailureScanResult Scan(TimeSpan window)
    {
        var events = new List<DnsFailureEvent>();
        bool operationalAvailable = true;
        long maxAgeMs = (long)Math.Max(1, window.TotalMilliseconds);

        try
        {
            var query = new EventLogQuery(OperationalChannel, PathType.LogName,
                $"*[System[TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.Id != TimeoutEventId) continue;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered locally - a known, common gap

                    string queriedName = ExtractQueriedName(message);
                    string? resolverIp = ExtractResolverIp(record);
                    events.Add(new DnsFailureEvent(
                        record.TimeCreated ?? DateTime.MinValue, "DNS-Client timeout", queriedName, resolverIp, Truncate(message, 200)));
                }
            }
        }
        catch
        {
            // Channel disabled (the common case - it's an analytic log, off by default), access
            // denied, or absent entirely - all real, expected conditions, not a bug.
            operationalAvailable = false;
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

                    events.Add(new DnsFailureEvent(
                        record.TimeCreated ?? DateTime.MinValue, "System log (Dnscache)", "(not name-specific)", ExtractResolverIp(record), Truncate(message, 200)));
                }
            }
        }
        catch
        {
            // Best-effort - the Operational-channel results above still stand on their own.
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
            events.OrderByDescending(e => e.TimeUtc).ToList(), byName, byResolver, operationalAvailable);
    }

    private static string ExtractQueriedName(string message)
    {
        var match = QueriedNameFromMessageRegex.Match(message);
        return match.Success ? match.Groups[1].Value.TrimEnd('.') : "(unknown)";
    }

    /// <summary>Pulls the first IPv4-shaped token out of the raw event's own XML payload, if any -
    /// see this class's remarks for why this is a best-effort extraction from real event data
    /// rather than a documented field lookup.</summary>
    private static string? ExtractResolverIp(EventRecord record)
    {
        try
        {
            string xml = record.ToXml();
            var match = Ipv4Regex.Match(xml);
            return match.Success ? match.Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
