using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>One roaming/association event (#541), with its #542 decoded reason attached.</summary>
public sealed record WifiConnectionEvent(
    DateTime TimeUtc, int EventId, string Kind, string? Ssid, string? Bssid,
    /// <summary>#541: time connected, attached to the disconnect event that closed the session -
    /// null for every other row (association-phase events don't have a "duration" of their own).</summary>
    TimeSpan? Duration,
    /// <summary>#542: decoded reason/status text - null when the event carries neither a
    /// recognizable code nor a classifiable message (e.g. a plain "connected" event).</summary>
    string? ReasonText,
    string RawMessage);

/// <summary>#541's full scan result. <see cref="ChannelAvailable"/> is false when the
/// WLAN-AutoConfig/Operational channel couldn't be read at all - same "off by default on a stock
/// install" caveat DnsEventLogService/DhcpEventLogService already document for their own channels,
/// though this specific one ships enabled by default on most consumer Windows installs (it's the
/// log Windows' own "Network reset"/Wi-Fi troubleshooter reads), so an unavailable read here more
/// often means a locked-down/managed machine than a stock default.</summary>
public sealed record WifiEventScanResult(List<WifiConnectionEvent> Events, bool ChannelAvailable);

/// <summary>
/// Item #541: reads Microsoft-Windows-WLAN-AutoConfig/Operational - by a wide margin the single
/// highest-value Wi-Fi log Windows ships (connect/disconnect/roam/association-phase events with
/// SSID, BSSID and, per #542, a decodable reason) - into a chronological timeline. The app
/// currently reads none of it; every other Wi-Fi figure on this tab is either a live API/netsh
/// snapshot or a different provider's log (DHCP-Client, DNS-Client).
///
/// Same shape DhcpEventLogService/DnsEventLogService already establish for this tab's other
/// event-log scans (EventLogQuery + a bounded ReverseDirection read, degrade-to-unavailable on any
/// failure) - on-demand only, behind an explicit Scan button, never a timer, per CLAUDE.md's
/// event-log-scan convention.
///
/// Field extraction is a generic "walk the event's own XML for named &lt;Data Name="X"&gt; values"
/// pass rather than a hardcoded per-event-ID struct layout, since WLAN-AutoConfig's own schema
/// isn't documented as a stable contract this app can rely on precisely across Windows versions -
/// same tradeoff DnsEventLogService's resolver-IP extraction already takes. A field this scan can't
/// find in a given event's XML shows as "Unknown" rather than guessed.
/// </summary>
public static class WifiEventLogService
{
    private const string Channel = "Microsoft-Windows-WLAN-AutoConfig/Operational";
    private const int MaxEvents = 3000;

    // #541's five event groups.
    private const int EventConnect = 8001;
    private const int EventDisconnect = 8003;
    private const int EventAssociationStart = 11000;
    private const int EventAssociationCompleted = 11004;
    private const int EventAuthenticationCompleted = 11005;
    private const int EventRoam = 11010;

    private static readonly HashSet<int> InterestingIds = new() { EventConnect, EventDisconnect, EventAssociationStart, EventAssociationCompleted, EventAuthenticationCompleted, EventRoam };

    private static readonly Regex NamedDataRegex = new(@"<Data Name=""([^""]+)"">([^<]*)</Data>", RegexOptions.Compiled);
    private static readonly string[] SsidFieldNames = { "SSID", "SSIDSTR", "ProfileName" };
    private static readonly string[] BssidFieldNames = { "BSSID", "BSSIDSTR", "Dot11Bssid", "TargetBSSID", "PeerMac" };
    private static readonly string[] ReasonFieldNames = { "Reason", "ReasonCode", "FailureReason" };
    private static readonly string[] StatusFieldNames = { "Status", "StatusCode", "AssociationStatus" };

    public static WifiEventScanResult Scan(TimeSpan window)
    {
        var raw = new List<(EventRecord Record, string Message)>();
        bool available = true;
        try
        {
            long maxAgeMs = (long)Math.Max(1, window.TotalMilliseconds);
            var query = new EventLogQuery(Channel, PathType.LogName,
                $"*[System[TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxEvents && reader.ReadEvent() is { } record)
            {
                if (!InterestingIds.Contains(record.Id)) { record.Dispose(); continue; }
                count++;
                string message;
                try { message = record.FormatDescription() ?? string.Empty; }
                catch { message = string.Empty; } // provider's message file isn't registered locally - a known, common gap
                raw.Add((record, message));
            }
        }
        catch
        {
            // Channel disabled, access denied, or absent - a real, expected condition on some
            // machines, not a bug.
            available = false;
        }

        try
        {
            var events = BuildTimeline(raw);
            return new WifiEventScanResult(events, available);
        }
        finally
        {
            foreach (var (record, _) in raw) record.Dispose();
        }
    }

    private static List<WifiConnectionEvent> BuildTimeline(List<(EventRecord Record, string Message)> raw)
    {
        // Oldest-first so a #541 session's duration can be computed as (disconnect time - the
        // connect time that opened it) while walking forward.
        var ascending = raw
            .Select(r => (r.Record, r.Message, Fields: ExtractNamedFields(r.Record), Time: r.Record.TimeCreated ?? DateTime.MinValue))
            .OrderBy(r => r.Time)
            .ToList();

        var results = new List<WifiConnectionEvent>(ascending.Count);
        DateTime? sessionStartUtc = null;

        foreach (var e in ascending)
        {
            string? ssid = FirstNonEmpty(e.Fields, SsidFieldNames);
            string? bssid = FirstNonEmpty(e.Fields, BssidFieldNames);
            string? reasonRaw = FirstNonEmpty(e.Fields, ReasonFieldNames);
            string? statusRaw = FirstNonEmpty(e.Fields, StatusFieldNames);

            TimeSpan? duration = null;
            if (e.Record.Id == EventConnect)
            {
                sessionStartUtc = e.Time;
            }
            else if (e.Record.Id == EventDisconnect)
            {
                if (sessionStartUtc is { } start && e.Time >= start) duration = e.Time - start;
                sessionStartUtc = null;
            }

            string? reasonText = e.Record.Id switch
            {
                EventDisconnect => WifiReasonCodeLookup.Decode(reasonRaw, isStatusCode: false, e.Message),
                EventAssociationCompleted or EventAuthenticationCompleted => WifiReasonCodeLookup.Decode(statusRaw ?? reasonRaw, isStatusCode: statusRaw is not null, e.Message),
                _ => null,
            };

            results.Add(new WifiConnectionEvent(e.Time, e.Record.Id, KindFor(e.Record.Id), ssid, bssid, duration, reasonText, Truncate(e.Message, 240)));
        }

        // Display order matches every other event-log table in this app: most recent first.
        results.Reverse();
        return results;
    }

    private static string KindFor(int id) => id switch
    {
        EventConnect => "Connected",
        EventDisconnect => "Disconnected",
        EventAssociationStart => "Association starting",
        EventAssociationCompleted => "Association completed",
        EventAuthenticationCompleted => "Authentication completed",
        EventRoam => "Roamed",
        _ => $"Event {id}",
    };

    private static Dictionary<string, string> ExtractNamedFields(EventRecord record)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string xml = record.ToXml();
            foreach (Match m in NamedDataRegex.Matches(xml))
                if (!dict.ContainsKey(m.Groups[1].Value))
                    dict[m.Groups[1].Value] = m.Groups[2].Value;
        }
        catch
        {
            // Best-effort - the row still shows with whatever the formatted message alone provides.
        }
        return dict;
    }

    private static string? FirstNonEmpty(Dictionary<string, string> fields, string[] candidateNames)
    {
        foreach (var name in candidateNames)
            if (fields.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        return null;
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
