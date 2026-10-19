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
/// The item names request timeout, reconnect and connectivity loss as the events that matter; the
/// IDs below are the ones the SMBClient provider's own manifest defines for each (the item's
/// "1016-class" IDs don't exist in that provider). Matched primarily by ID (a real, stable contract for this
/// specific provider, unlike DHCP/DNS-Client's own less-documented message text), with a keyword
/// fallback bucket for any other event in the two channels so a build where an ID shifted still
/// shows up as "Other" instead of silently vanishing.
/// </summary>
public static class SmbClientEventLogService
{
    private const string ConnectivityChannel = "Microsoft-Windows-SMBClient/Connectivity";
    private const string OperationalChannel = "Microsoft-Windows-SMBClient/Operational";
    private const int MaxEventsPerChannel = 1000;

    // IDs from the provider's own manifest (Get-WinEvent -ListProvider Microsoft-Windows-SMBClient).
    // 30809: a request timed out with no response from the server.
    private static readonly int[] TimeoutIds = { 30809 };
    // 30806: session re-established; 30808: share connection re-established.
    private static readonly int[] ReconnectIds = { 30806, 30808 };
    // 30803: couldn't establish a connection; 30804: connection disconnected; 30805: session
    // lost; 30807: share connection lost. (30810 - "added a transport interface" - is routine.)
    private static readonly int[] ConnectivityLossIds = { 30803, 30804, 30805, 30807 };

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
            // Channel disabled by policy, access denied, or absent entirely - all real, expected
            // conditions (both are Operational channels, normally enabled).
            return false;
        }
    }

    private static string Categorize(int id, string message)
    {
        // A known ID always wins - these messages' "Guidance" text mentions timeouts and
        // reconnects in passing, so keyword matching is only the fallback for unknown IDs.
        if (TimeoutIds.Contains(id)) return "Session timeout";
        if (ReconnectIds.Contains(id)) return "Reconnect";
        if (ConnectivityLossIds.Contains(id)) return "Connectivity loss";

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) || message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "Session timeout";
        if (message.Contains("reconnect", StringComparison.OrdinalIgnoreCase) || message.Contains("re-established", StringComparison.OrdinalIgnoreCase))
            return "Reconnect";
        if (message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) || message.Contains("lost connectivity", StringComparison.OrdinalIgnoreCase))
            return "Connectivity loss";
        return "Other";
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
