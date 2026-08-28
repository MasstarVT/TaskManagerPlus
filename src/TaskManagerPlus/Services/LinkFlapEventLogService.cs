using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>One System-log link-state/reset event (#548). NDIS's miniport link-state events use a
/// small, standard set of event IDs (27 = "the network link is disconnected", 32/33 = link state
/// change) that every vendor's own driver provider (e1dexpress, rt640x64, Netwtw10, ...) logs under
/// its own provider name rather than a shared "NDIS" provider - that's exactly why this scans by
/// EventID across the whole System log instead of a fixed provider name, the same "vendor identity
/// varies, the event ID contract doesn't" tradeoff <see cref="ProviderName"/> exists to surface
/// rather than hide.</summary>
public sealed record LinkFlapEvent(DateTime TimeUtc, int EventId, string ProviderName, string Kind, string? AdapterHint, string Message);

/// <summary><see cref="ResetCount"/> is the subset of <see cref="Events"/> that look like an actual
/// down/reset transition (event 27, or a 32/33 whose text reads as a disconnect) rather than a
/// clean "link came up" entry - the number #548 asks this scan to turn into a driver-level fact.</summary>
public sealed record LinkFlapScanResult(List<LinkFlapEvent> Events, int ResetCount, bool ChannelAvailable);

/// <summary>
/// Item #548: scans the System log for NIC miniport link-state/reset events over a lookback window,
/// on-demand only (behind an explicit Scan button in the Adapter health card), never on a timer -
/// same event-log-scan convention DhcpEventLogService/IpConflictService/WifiEventLogService already
/// establish for this tab's other on-demand scans. "Turns intermittent dropouts into a driver-level
/// fact" (per the suggestion text) rather than a user's vague "the internet keeps cutting out"
/// impression.
/// </summary>
public static class LinkFlapEventLogService
{
    private const int EventLinkDisconnected = 27;
    private const int EventLinkStateA = 32;
    private const int EventLinkStateB = 33;
    private const int MaxEvents = 2000;

    private static readonly Regex NamedDataRegex = new(@"<Data Name=""([^""]+)"">([^<]*)</Data>", RegexOptions.Compiled);
    private static readonly string[] AdapterHintFieldNames = { "InterfaceDescription", "AdapterFriendlyName", "Description", "DeviceDescription", "Name" };

    public static LinkFlapScanResult Scan(TimeSpan window)
    {
        var events = new List<LinkFlapEvent>();
        bool available = true;
        try
        {
            long maxAgeMs = (long)Math.Max(1, window.TotalMilliseconds);
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[(EventID={EventLinkDisconnected} or EventID={EventLinkStateA} or EventID={EventLinkStateB}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
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

                    string providerName = record.ProviderName ?? "Unknown";
                    var fields = ExtractNamedFields(record);
                    string? adapterHint = FirstNonEmpty(fields, AdapterHintFieldNames);

                    events.Add(new LinkFlapEvent(record.TimeCreated ?? DateTime.MinValue, record.Id, providerName,
                        KindFor(record.Id, message), adapterHint, Truncate(message, 260)));
                }
            }
        }
        catch
        {
            // Access denied, or (very unlikely for the System log itself) unavailable - degrade to
            // "couldn't scan" rather than throwing into the caller's command handler.
            available = false;
        }

        int resetCount = events.Count(e => LooksLikeReset(e));
        return new LinkFlapScanResult(events.OrderByDescending(e => e.TimeUtc).ToList(), resetCount, available);
    }

    private static bool LooksLikeReset(LinkFlapEvent e)
        => e.EventId == EventLinkDisconnected ||
           e.Message.Contains("disconnect", StringComparison.OrdinalIgnoreCase) ||
           e.Message.Contains(" down", StringComparison.OrdinalIgnoreCase);

    private static string KindFor(int id, string message)
    {
        if (id == EventLinkDisconnected) return "Link down";
        bool looksUp = message.Contains("connected", StringComparison.OrdinalIgnoreCase) &&
                       !message.Contains("disconnect", StringComparison.OrdinalIgnoreCase);
        return looksUp ? "Link state: up" : "Link state change";
    }

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
