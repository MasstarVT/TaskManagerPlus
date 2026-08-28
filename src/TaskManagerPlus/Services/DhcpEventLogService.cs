using System.Diagnostics.Eventing.Reader;

namespace TaskManagerPlus.Services;

/// <summary>One DHCP client event (#530), loosely categorized from its formatted message text.</summary>
public sealed record DhcpClientEvent(DateTime TimeUtc, string Channel, string Category, string Message);

/// <summary>#530's full scan result. <see cref="OperationalChannelAvailable"/> is false when
/// Microsoft-Windows-Dhcp-Client/Operational couldn't be read - like DNS-Client/Operational (#524),
/// it's an analytic channel disabled by default on a stock Windows install, the single most common
/// reason this half of the scan comes back empty, not a bug.</summary>
public sealed record DhcpEventScanResult(List<DhcpClientEvent> Events, bool AdminChannelAvailable, bool OperationalChannelAvailable);

/// <summary>
/// Item #530: scans Microsoft-Windows-Dhcp-Client/Admin and /Operational for lease-acquisition
/// failures, NACKs and conflicts over a caller-chosen lookback window - turning "it randomly loses
/// the connection at night" into a timestamped lease-renewal failure instead of a vague symptom.
///
/// Same shape DnsEventLogService (#524) already establishes for this tab's other event-log scan -
/// EventLogQuery + a bounded ReverseDirection read, degrade-to-unavailable on any failure - a fresh
/// class rather than extending DnsEventLogService since this reads an entirely different provider
/// with no ID overlap. On-demand only, behind an explicit "Scan" button (CLAUDE.md's
/// on-demand-for-event-log-scans convention), never a timer.
///
/// The DHCP client provider's own event IDs/message formats aren't documented as a stable public
/// contract this app can rely on precisely, so events are grouped into buckets by keyword search
/// over the formatted message text (NACK/declined, conflict, other failure) rather than a hardcoded
/// ID table that could silently miss a build where the ID shifted - the same "best-effort
/// classification of real event data, not a fabricated ID mapping" tradeoff DnsEventLogService's own
/// resolver-IP extraction already takes.
/// </summary>
public static class DhcpEventLogService
{
    private const string AdminChannel = "Microsoft-Windows-Dhcp-Client/Admin";
    private const string OperationalChannel = "Microsoft-Windows-Dhcp-Client/Operational";
    private const int MaxEventsPerChannel = 1000;

    private static readonly string[] ConflictKeywords = { "conflict", "already in use", "duplicate address" };
    private static readonly string[] NackKeywords = { "NACK", "declined", "denied", "refused" };
    private static readonly string[] FailureKeywords = { "fail", "not assigned", "unable", "timed out", "timeout", "no DHCP", "APIPA", "could not" };

    public static DhcpEventScanResult Scan(TimeSpan window)
    {
        var events = new List<DhcpClientEvent>();
        bool adminOk = TryScanChannel(AdminChannel, "Admin", window, events);
        bool operationalOk = TryScanChannel(OperationalChannel, "Operational", window, events);
        return new DhcpEventScanResult(events.OrderByDescending(e => e.TimeUtc).ToList(), adminOk, operationalOk);
    }

    private static bool TryScanChannel(string channel, string label, TimeSpan window, List<DhcpClientEvent> sink)
    {
        try
        {
            long maxAgeMs = (long)Math.Max(1, window.TotalMilliseconds);
            var query = new EventLogQuery(channel, PathType.LogName,
                $"*[System[TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxEventsPerChannel && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered locally - a known, common gap

                    sink.Add(new DhcpClientEvent(record.TimeCreated ?? DateTime.MinValue, label, Categorize(message), Truncate(message, 240)));
                }
            }
            return true;
        }
        catch
        {
            // Channel disabled (the common case for Operational - an analytic log, off by default),
            // access denied, or absent entirely - all real, expected conditions, not a bug.
            return false;
        }
    }

    private static string Categorize(string message)
    {
        if (ConflictKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase))) return "Conflict";
        if (NackKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase))) return "NACK / declined";
        if (FailureKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase))) return "Lease failure";
        return "Other";
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
