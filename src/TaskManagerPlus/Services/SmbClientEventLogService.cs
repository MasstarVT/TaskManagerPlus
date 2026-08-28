using System.Diagnostics.Eventing.Reader;

namespace TaskManagerPlus.Services;

/// <summary>One SMB client session-drop/reconnect/timeout event (#589).</summary>
public sealed record SmbClientEvent(DateTime TimeUtc, string Channel, int EventId, string Category, string Message);

/// <summary>#589's full scan result - same "channel may just be disabled by default" honesty
/// DnsEventLogService/DhcpEventLogService already establish for their own scans.</summary>
public sealed record SmbClientEventScanResult(List<SmbClientEvent> Events, bool ConnectivityChannelAvailable, bool OperationalChannelAvailable);

/// <summary>
/// Item #589 (suggestions.md "SMB and network drives"): scans
/// Microsoft-Windows-SMBClient/Connectivity and /Operational for session-drop, reconnect and
/// timeout events, giving a timeline of when a share actually went away rather than the vague "it
/// just stopped working" a user otherwise reports. Same EventLogQuery + bounded ReverseDirection
/// shape DhcpEventLogService (#530) already establishes for this app's other on-demand event-log
/// scans - see that class's remarks for the general rationale (on-demand only, per CLAUDE.md's
/// event-log-scan convention; a fresh class per provider rather than sharing one, since there's no
/// ID overlap between providers).
///
/// The item names specific event IDs (30803/30809/1016-class) as the ones that matter - session
/// disconnect-due-to-timeout, reconnect, and a small family of connectivity-loss IDs Microsoft's
/// own SMBClient provider documents. Matched primarily by ID (a real, stable contract for this
/// specific provider, unlike DHCP/DNS-Client's own less-documented message text), with a keyword
/// fallback bucket for any other event in the two channels so a build where an ID shifted still
/// shows up as "Other" instead of silently vanishing.
/// </summary>
public static class SmbClientEventLogService
{
    private const string ConnectivityChannel = "Microsoft-Windows-SMBClient/Connectivity";
    private const string OperationalChannel = "Microsoft-Windows-SMBClient/Operational";
    private const int MaxEventsPerChannel = 1000;

    // Session disconnected because the server stopped responding within the configured timeout.
    private static readonly int[] TimeoutIds = { 30803, 30804 };
    // Session/share reconnected after a prior drop.
    private static readonly int[] ReconnectIds = { 30809, 30810 };
    // The general "1016-class" connectivity-loss family the item text references.
    private static readonly int[] ConnectivityLossIds = { 1016, 1017, 1018 };

    public static SmbClientEventScanResult Scan(TimeSpan window)
    {
        var events = new List<SmbClientEvent>();
        bool connectivityOk = TryScanChannel(ConnectivityChannel, window, events);
        bool operationalOk = TryScanChannel(OperationalChannel, window, events);
        return new SmbClientEventScanResult(events.OrderByDescending(e => e.TimeUtc).ToList(), connectivityOk, operationalOk);
    }

    private static bool TryScanChannel(string channel, TimeSpan window, List<SmbClientEvent> sink)
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

                    int id = record.Id;
                    sink.Add(new SmbClientEvent(record.TimeCreated ?? DateTime.MinValue, channel, id, Categorize(id, message), Truncate(message, 260)));
                }
            }
            return true;
        }
        catch
        {
            // Channel disabled (both of these are analytic/debug logs, off by default on a stock
            // install), access denied, or absent entirely - all real, expected conditions.
            return false;
        }
    }

    private static string Categorize(int id, string message)
    {
        if (TimeoutIds.Contains(id) || message.Contains("timed out", StringComparison.OrdinalIgnoreCase) || message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "Session timeout";
        if (ReconnectIds.Contains(id) || message.Contains("reconnect", StringComparison.OrdinalIgnoreCase))
            return "Reconnect";
        if (ConnectivityLossIds.Contains(id) || message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) || message.Contains("lost connectivity", StringComparison.OrdinalIgnoreCase))
            return "Connectivity loss";
        return "Other";
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
