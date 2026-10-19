using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>One connection/association event (#541), with its #542 reason attached.</summary>
public sealed record WifiConnectionEvent(
    DateTime TimeUtc, int EventId, string Kind, string? Ssid, string? Bssid,
    /// <summary>#541: time connected, attached to the disconnect event that closed the session -
    /// null for every other row (association-phase events don't have a "duration" of their own).</summary>
    TimeSpan? Duration,
    /// <summary>#542: the reason Windows logged for a disconnect or failure (plus a decoded 802.11
    /// status code where the event carries one) - null for success/start events.</summary>
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
/// highest-value Wi-Fi log Windows ships (connect/disconnect/association/security events with the
/// SSID and, on the disconnect and failure events, Windows' own reason - #542) - into a
/// chronological timeline. The app
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

    // IDs and template fields as the WLAN-AutoConfig manifest defines them (Get-WinEvent
    // -ListProvider). The channel has no roam event, and 11004/11010 are only the "security
    // stopped/started" bookends. The three failure events are the ones that carry a reason.
    private const int EventConnect = 8001;
    private const int EventConnectFailed = 8002;
    private const int EventDisconnect = 8003;
    private const int EventAssociationStart = 11000;
    private const int EventAssociationSucceeded = 11001;
    private const int EventAssociationFailed = 11002;
    private const int EventSecuritySucceeded = 11005;
    private const int EventSecurityFailed = 11006;

    private static readonly HashSet<int> InterestingIds = new()
    {
        EventConnect, EventConnectFailed, EventDisconnect, EventAssociationStart,
        EventAssociationSucceeded, EventAssociationFailed, EventSecuritySucceeded, EventSecurityFailed,
    };

    // EventRecord.ToXml() writes single-quoted attributes (<Data Name='SSID'>); the old
    // double-quote-only pattern never matched, so every field read as missing.
    private static readonly Regex NamedDataRegex = new(@"<Data Name=(['""])([^'""]+)\1>([^<]*)</Data>", RegexOptions.Compiled);
    private static readonly string[] SsidFieldNames = { "SSID", "ProfileName" };
    // Only 11006 (security failed) names the peer AP; no other event read here has a BSSID field.
    private static readonly string[] BssidFieldNames = { "PeerMac" };
    // Windows' own reason text: 8003 "Reason", 8002/11002 "FailureReason", 11006 "ReasonText".
    // Their ReasonCode fields are WLAN_REASON_CODE values, not 802.11 codes, so they aren't decoded.
    private static readonly string[] ReasonTextFieldNames = { "Reason", "FailureReason", "ReasonText" };
    private static readonly string[] Dot11StatusFieldNames = { "Dot11StatusCode" };

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
            string? reasonRaw = FirstNonEmpty(e.Fields, ReasonTextFieldNames);

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

            // Success and start events carry no reason, so nothing is decoded for them.
            string? reasonText = e.Record.Id switch
            {
                EventDisconnect or EventConnectFailed or EventSecurityFailed => WifiReasonCodeLookup.Describe(reasonRaw, null),
                EventAssociationFailed => WifiReasonCodeLookup.Describe(reasonRaw, FirstNonEmpty(e.Fields, Dot11StatusFieldNames)),
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
        EventConnectFailed => "Connection failed",
        EventDisconnect => "Disconnected",
        EventAssociationStart => "Association starting",
        EventAssociationSucceeded => "Association succeeded",
        EventAssociationFailed => "Association failed",
        EventSecuritySucceeded => "Security succeeded",
        EventSecurityFailed => "Security failed",
        _ => $"Event {id}",
    };

    private static Dictionary<string, string> ExtractNamedFields(EventRecord record)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string xml = record.ToXml();
            foreach (Match m in NamedDataRegex.Matches(xml))
                if (!dict.ContainsKey(m.Groups[2].Value))
                    dict[m.Groups[2].Value] = System.Net.WebUtility.HtmlDecode(m.Groups[3].Value);
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
