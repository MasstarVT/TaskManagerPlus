using System.Diagnostics.Eventing.Reader;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #287: Search indexer crawl-state detail - on-demand only (an event-log scan), complementing
/// BackgroundActivityService.ReadSearchIndexerLiveState's cheap already-polled-process cost. The
/// Microsoft-Windows-Search/Operational channel documents no stable, versioned crawl-start/stop
/// event-ID contract (unlike Defender's 1000/1001/1116/1117), so - the same honest fallback
/// WifiScanStormService's own operational-log signal already takes - this looks for "crawl" in the
/// formatted event text rather than keying off specific IDs, and degrades to "log active, but no
/// explicit crawl text found" rather than a guessed in-progress state.
/// </summary>
public static class SearchIndexerActivityService
{
    private const string OperationalLog = "Microsoft-Windows-Search/Operational";
    private const string BackOffKeyPath = @"SOFTWARE\Microsoft\Windows Search";
    private const string WorkingSetRulesPath = @"SOFTWARE\Microsoft\Windows Search\CrawlScopeManager\Windows\SystemIndex\WorkingSetRules";

    public static async Task<SearchIndexerCrawlResult> ReadCrawlStateAsync(TimeSpan window) => await Task.Run(() =>
    {
        DateTime? lastStart = null, lastStop = null;
        bool logAvailable = true;
        int totalEvents = 0;
        try
        {
            long maxAgeMs = (long)window.TotalMilliseconds;
            var query = new EventLogQuery(OperationalLog, PathType.LogName,
                $"*[System[TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 2000;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    totalEvents++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    if (message.IndexOf("crawl", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var time = record.TimeCreated ?? DateTime.MinValue;

                    bool looksLikeStart = message.IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           message.IndexOf("begin", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool looksLikeStop = message.IndexOf("stop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          message.IndexOf("complet", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          message.IndexOf("finish", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (looksLikeStart && (lastStart is null || time > lastStart)) lastStart = time;
                    if (looksLikeStop && (lastStop is null || time > lastStop)) lastStop = time;
                }
            }
        }
        catch
        {
            logAvailable = false;
        }

        bool crawlLikelyInProgress = lastStart is { } s && (lastStop is null || s > lastStop);

        string status = !logAvailable
            ? $"{OperationalLog} isn't available on this system (or access was denied)."
            : lastStart is null && lastStop is null
                ? (totalEvents == 0
                    ? "No Search operational events found in the lookback window."
                    : $"{totalEvents} Search operational event(s) found, but none mentioned a crawl start/stop by name - can't tell whether a crawl is in progress from this log alone.")
                : crawlLikelyInProgress
                    ? $"A crawl looks to be in progress - the most recent crawl-start signal ({lastStart:T}) is newer than the most recent crawl-stop signal."
                    : $"No crawl currently in progress - the most recent crawl-stop signal ({lastStop:T}) is newer than the most recent crawl-start signal.";

        return new SearchIndexerCrawlResult
        {
            IsAvailable = logAvailable,
            StatusText = status,
            CrawlLikelyInProgress = crawlLikelyInProgress,
            LastCrawlStart = lastStart,
            LastCrawlStop = lastStop,
            IndexedLocations = ReadIndexedLocations(),
            BackOffDelayMs = ReadBackOffHint(),
        };
    });

    /// <summary>Best-effort included-location list from the CrawlScopeManager's WorkingSetRules
    /// registry tree - an undocumented-but-commonly-referenced location, not a public API, so any
    /// structural mismatch (missing subkeys/values) just yields an empty list rather than throwing.</summary>
    private static List<string> ReadIndexedLocations()
    {
        var locations = new List<string>();
        try
        {
            using var rulesKey = Registry.LocalMachine.OpenSubKey(WorkingSetRulesPath);
            if (rulesKey is null) return locations;

            foreach (var subName in rulesKey.GetSubKeyNames())
            {
                try
                {
                    using var sub = rulesKey.OpenSubKey(subName);
                    if (sub?.GetValue("URL") is string url && sub.GetValue("IsIncluded") is int included && included != 0)
                        locations.Add(url);
                }
                catch
                {
                    // One malformed rule shouldn't stop the rest of the scan.
                }
            }
        }
        catch
        {
            // Key unavailable/access denied - empty list.
        }
        return locations.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The indexer's registry tree carries no single, stable "back-off delay" value name
    /// documented by Microsoft - this reads whatever DWORD throttling-shaped value happens to be
    /// present directly under the Windows Search key as informational context, never asserting a
    /// specific meaning it can't back up.</summary>
    private static int? ReadBackOffHint()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(BackOffKeyPath);
            if (key is null) return null;
            foreach (var name in key.GetValueNames())
            {
                if (name.IndexOf("backoff", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("throttle", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (key.GetValue(name) is int i) return i;
            }
        }
        catch
        {
            // Key unavailable/access denied.
        }
        return null;
    }
}
