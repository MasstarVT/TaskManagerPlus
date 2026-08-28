using System.Diagnostics.Eventing.Reader;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #127-134: error-burst and anomaly detection over the event log - one new service backing the
/// Events tab's "Anomaly detection" panel (and, for #128/#130, reused by the Stability tab off data
/// it already reads). Every entry point here is meant to be called from an explicit deep-scan
/// button via Task.Run, never a DispatcherTimer - a 90-day multi-channel read is exactly the kind
/// of "not cheap enough to repeat on a tick" work CLAUDE.md's on-demand rule is about. Every flag
/// produced is a statistical/pattern heuristic, not a diagnosis (CLAUDE.md's "quick flag, not a
/// verdict" convention) - degrades to "no flags" on too little data rather than guessing.
/// </summary>
public sealed class EventAnomalyDetectionService
{
    private readonly EventLogExplorerService _explorer;

    public EventAnomalyDetectionService(EventLogExplorerService explorer) => _explorer = explorer;

    // ==== Deep scan reader (feeds #127/#128/#129/#130/#132/#133/#134) ====

    public sealed class DeepScanResult
    {
        public List<EventRecordRow> Rows { get; init; } = new();
        public string? ErrorText { get; init; }
        public bool WasCapped { get; init; }
    }

    /// <summary>Reads every matching record across <paramref name="channels"/> up to <paramref
    /// name="maxRecords"/>, paging through EventLogExplorerService.ReadMultiChannel until the query
    /// runs dry, the cap is hit, or the caller cancels. This is the one "expensive" read every
    /// anomaly-detection method below is computed from - callers run it once per deep scan and reuse
    /// the resulting list for every #127-134 computation rather than re-querying the log per
    /// heuristic.</summary>
    public DeepScanResult ReadWindow(IReadOnlyList<string> channels, string xpath, int maxRecords, IProgress<int>? progress, CancellationToken ct)
    {
        var rows = new List<EventRecordRow>();
        if (channels.Count == 0) return new DeepScanResult { ErrorText = "No channels selected." };

        string structuredXml = EventLogExplorerService.BuildStructuredQuery(channels, xpath);
        EventBookmark? bookmark = null;
        try
        {
            while (rows.Count < maxRecords)
            {
                ct.ThrowIfCancellationRequested();
                var page = _explorer.ReadMultiChannel(structuredXml, bookmark, pageSize: 500);
                if (page.ErrorText is not null)
                    return new DeepScanResult { Rows = rows, ErrorText = page.ErrorText };
                if (page.Rows.Count == 0) break;

                rows.AddRange(page.Rows);
                bookmark = page.Bookmark;
                progress?.Report(rows.Count);
                if (!page.HasMore) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new DeepScanResult { Rows = rows, ErrorText = ex.Message };
        }

        return new DeepScanResult { Rows = rows, WasCapped = rows.Count >= maxRecords };
    }

    // ==== #127: per-event-ID baseline with spike flagging ====

    /// <summary>Builds a daily-count history per (provider, eventId) across whatever window was
    /// scanned, then flags any signature whose last-24-hour count exceeds its own median by a
    /// robust margin (scaled median absolute deviation, i.e. MAD * 1.4826 as a standard-deviation
    /// stand-in that isn't skewed by the occasional huge day the way a mean/stddev would be). A
    /// floor guard (median + 4, when the robust deviation itself is near zero) keeps a normally-
    /// silent ID that fires once or twice from being flagged as "unusual" - going from 0 to 1 isn't
    /// a spike.</summary>
    public List<EventIdBaselineFlag> ComputeBaselineFlags(IEnumerable<EventRecordRow> rows, DateTime now, int minOccurrences = 5)
    {
        var result = new List<EventIdBaselineFlag>();
        var last24hStart = now.AddHours(-24);

        foreach (var group in rows.GroupBy(r => (r.ProviderName, r.EventId)))
        {
            var times = group.Select(r => r.TimeCreated).OrderBy(t => t).ToList();
            if (times.Count < minOccurrences) continue;

            var byDay = times.GroupBy(t => t.Date).ToDictionary(g => g.Key, g => g.Count());
            var dailyCounts = new List<double>();
            for (var d = times[0].Date; d <= now.Date; d = d.AddDays(1))
                dailyCounts.Add(byDay.TryGetValue(d, out var c) ? c : 0);

            double median = Median(dailyCounts);
            double mad = Median(dailyCounts.Select(c => Math.Abs(c - median)).ToList());
            double robustDeviation = mad * 1.4826;
            double threshold = robustDeviation > 0.5 ? median + 3 * robustDeviation : median + 4;

            int last24hCount = times.Count(t => t >= last24hStart);
            if (last24hCount > threshold && last24hCount >= 3)
            {
                result.Add(new EventIdBaselineFlag
                {
                    Provider = group.Key.ProviderName,
                    EventId = group.Key.EventId,
                    Last24HourCount = last24hCount,
                    MedianDailyCount = median,
                    RobustDeviation = robustDeviation,
                    ObservedDays = dailyCounts.Count,
                    SampleMessage = group.OrderByDescending(r => r.TimeCreated).First().Message,
                });
            }
        }

        return result.OrderByDescending(f => f.Last24HourCount - f.MedianDailyCount).ToList();
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }

    // ==== #128: first-ever-occurrence detection ====

    /// <summary>Flags every (provider, eventId) signature whose only occurrences in <paramref
    /// name="occurrences"/> fall within the last <paramref name="recentWindowDays"/> days - i.e. it
    /// never showed up in the older portion of whatever was scanned. This is "first-ever within the
    /// scanned window," not a claim about the entire lifetime of the machine (event logs have finite
    /// retention - see #131's log-churn feature for exactly why) - callers should scan a window
    /// meaningfully longer than <paramref name="recentWindowDays"/> for this to mean anything (a
    /// 7-day scan would trivially flag everything).</summary>
    public List<FirstOccurrenceFlag> ComputeFirstOccurrences(
        IEnumerable<(string Provider, int EventId, DateTime TimeCreated, string? Message)> occurrences,
        DateTime now, int recentWindowDays = 7)
    {
        var recentCutoff = now.AddDays(-recentWindowDays);
        var result = new List<FirstOccurrenceFlag>();

        foreach (var group in occurrences.GroupBy(o => (o.Provider, o.EventId)))
        {
            var items = group.OrderBy(o => o.TimeCreated).ToList();
            if (items[0].TimeCreated < recentCutoff) continue; // has older history - not a first occurrence

            result.Add(new FirstOccurrenceFlag
            {
                Provider = group.Key.Provider,
                EventId = group.Key.EventId,
                FirstSeen = items[0].TimeCreated,
                OccurrenceCount = items.Count,
                SampleMessage = items[0].Message ?? string.Empty,
            });
        }

        return result.OrderByDescending(f => f.FirstSeen).ToList();
    }

    // ==== #129: burst collapsing ====

    /// <summary>Clusters consecutive occurrences of the same (provider, eventId) signature into one
    /// BurstGroup wherever the gap between consecutive occurrences stays within <paramref
    /// name="window"/> and the resulting run has at least <paramref name="minCount"/> members - e.g.
    /// 20+ within 5 minutes collapses into one incident row instead of flooding a grid with near-
    /// identical rows (and skewing #127's daily-count baseline while it's at it). Rows that never
    /// form a big-enough run simply aren't returned - callers show the leftover raw rows separately,
    /// or ignore them, depending on the view.</summary>
    public List<BurstGroup> CollapseBursts(IEnumerable<EventRecordRow> rows, TimeSpan window, int minCount)
    {
        var groups = new List<BurstGroup>();

        foreach (var bySignature in rows.GroupBy(r => (r.ProviderName, r.EventId)))
        {
            var ordered = bySignature.OrderBy(r => r.TimeCreated).ToList();
            int i = 0;
            while (i < ordered.Count)
            {
                int j = i;
                while (j + 1 < ordered.Count && (ordered[j + 1].TimeCreated - ordered[j].TimeCreated) <= window)
                    j++;

                int runLength = j - i + 1;
                if (runLength >= minCount)
                {
                    var slice = ordered.GetRange(i, runLength);
                    groups.Add(new BurstGroup
                    {
                        Provider = bySignature.Key.ProviderName,
                        EventId = bySignature.Key.EventId,
                        Level = slice[0].Level,
                        Count = runLength,
                        FirstTime = slice[0].TimeCreated,
                        LastTime = slice[^1].TimeCreated,
                        SampleMessage = slice[0].Message,
                        Rows = slice,
                    });
                }
                i = j + 1;
            }
        }

        return groups.OrderByDescending(g => g.Count).ToList();
    }

    // ==== #130: error-density heatmap ====

    /// <summary>Builds a zero-filled day x hour-of-day grid (every day from the earliest to the
    /// latest timestamp in <paramref name="times"/>, all 24 hours each) - "day" is the actual
    /// chronological calendar date, not a 1-31 day-of-month bucket, so a real pattern like "every
    /// night at 3 AM" (a vertical stripe at hour=3 across many days) or "only after resume on the
    /// 14th" (one dense day) is visible rather than folded across different months.</summary>
    public List<ErrorDensityHeatmapCell> ComputeDensityHeatmap(IEnumerable<DateTime> times)
    {
        var byCell = new Dictionary<(DateTime Day, int Hour), int>();
        foreach (var t in times)
        {
            var key = (t.Date, t.Hour);
            byCell[key] = byCell.GetValueOrDefault(key) + 1;
        }
        if (byCell.Count == 0) return new List<ErrorDensityHeatmapCell>();

        var minDay = byCell.Keys.Min(k => k.Day);
        var maxDay = byCell.Keys.Max(k => k.Day);

        var result = new List<ErrorDensityHeatmapCell>();
        for (var d = minDay; d <= maxDay; d = d.AddDays(1))
            for (int h = 0; h < 24; h++)
                result.Add(new ErrorDensityHeatmapCell { Day = d, Hour = h, Count = byCell.GetValueOrDefault((d, h)) });

        return result;
    }

    // ==== #131: log churn attribution ====

    public sealed class ProviderChurnScanResult
    {
        public List<ProviderChurnRow> Rows { get; init; } = new();
        public int TotalRecordsScanned { get; init; }
        public bool WasCapped { get; init; }
        public string? ErrorText { get; init; }
    }

    /// <summary>Ranks providers by how many records they wrote into one channel within <paramref
    /// name="lookbackDays"/> - the direct "why does my System log only go back 2 days" answer. Uses
    /// a raw EventLogReader pass that reads only the provider name (never FormatDescription/ToXml,
    /// unlike EventLogExplorerService.ConvertRecord) so it stays fast even against a high-volume
    /// channel with hundreds of thousands of records in range - #103's ReadPage/#112's
    /// ReadMultiChannel would be far slower here since every record they touch is fully formatted
    /// whether or not this scan cares about its text.</summary>
    public ProviderChurnScanResult ScanProviderChurn(string channelName, int lookbackDays, int maxRecords, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        bool capped = false;

        try
        {
            string xpath = lookbackDays > 0
                ? $"*[System[TimeCreated[timediff(@SystemTime) <= {lookbackDays * 24L * 60 * 60 * 1000}]]]"
                : "*";
            var query = new EventLogQuery(channelName, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);

            while (total < maxRecords)
            {
                if (total % 500 == 0) ct.ThrowIfCancellationRequested();

                using var record = reader.ReadEvent();
                if (record is null) break;
                total++;

                string provider;
                try { provider = record.ProviderName ?? "Unknown"; }
                catch { provider = "Unknown"; }
                counts[provider] = counts.GetValueOrDefault(provider) + 1;
            }

            if (total >= maxRecords)
            {
                using var probe = reader.ReadEvent();
                capped = probe is not null;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ProviderChurnScanResult { ErrorText = ex.Message };
        }

        var rows = counts
            .Select(kv => new ProviderChurnRow
            {
                Channel = channelName,
                Provider = kv.Key,
                RecordCount = kv.Value,
                PercentOfTotal = total == 0 ? 0 : Math.Round(kv.Value * 100.0 / total, 1),
            })
            .OrderByDescending(r => r.RecordCount)
            .ToList();

        return new ProviderChurnScanResult { Rows = rows, TotalRecordsScanned = total, WasCapped = capped };
    }

    // ==== #132: periodic-loop detection ====

    /// <summary>Flags a signature whose inter-arrival intervals are near-constant - coefficient of
    /// variation (StdDev / Mean) at or below <paramref name="maxCoefficientOfVariation"/> - as a
    /// probable retry/restart loop, with the measured mean period. A service restarting every 60
    /// seconds and the same total count spread randomly across a day look identical to a bare
    /// count, but very different once the gaps between occurrences are examined.</summary>
    public List<PeriodicLoopFlag> DetectPeriodicLoops(IEnumerable<EventRecordRow> rows, int minOccurrences = 5, double maxCoefficientOfVariation = 0.15)
    {
        var result = new List<PeriodicLoopFlag>();

        foreach (var group in rows.GroupBy(r => (r.ProviderName, r.EventId)))
        {
            var times = group.Select(r => r.TimeCreated).OrderBy(t => t).ToList();
            if (times.Count < minOccurrences) continue;

            var intervals = new List<double>();
            for (int i = 1; i < times.Count; i++)
                intervals.Add((times[i] - times[i - 1]).TotalSeconds);

            double mean = intervals.Average();
            if (mean <= 0) continue;

            double variance = intervals.Sum(v => (v - mean) * (v - mean)) / intervals.Count;
            double stdDev = Math.Sqrt(variance);
            if (stdDev / mean > maxCoefficientOfVariation) continue;

            result.Add(new PeriodicLoopFlag
            {
                Provider = group.Key.ProviderName,
                EventId = group.Key.EventId,
                OccurrenceCount = times.Count,
                MeanIntervalSeconds = mean,
                StdDevSeconds = stdDev,
                FirstSeen = times[0],
                LastSeen = times[^1],
            });
        }

        return result.OrderBy(f => f.MeanIntervalSeconds).ToList();
    }

    // ==== #133: post-boot vs steady-state error profile ====

    /// <summary>Reads every EventLog 6005 ("The Event log service was started") and
    /// Microsoft-Windows-Kernel-General 12 (OS boot) record in the System channel within <paramref
    /// name="lookbackDays"/> - the same two boot markers used elsewhere in this app for "unexpected
    /// shutdown" detection (see EventLogService), here used the other direction: as the start of a
    /// "just booted" window rather than proof the previous shutdown was clean.</summary>
    public List<DateTime> FindBootMarkers(int lookbackDays, CancellationToken ct)
    {
        string xpath = $"*[System[TimeCreated[timediff(@SystemTime) <= {lookbackDays * 24L * 60 * 60 * 1000}] and ((Provider[@Name='EventLog'] and EventID=6005) or (Provider[@Name='Microsoft-Windows-Kernel-General'] and EventID=12))]]";

        var markers = new List<DateTime>();
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < 2000)
            {
                if (count % 200 == 0) ct.ThrowIfCancellationRequested();
                using var record = reader.ReadEvent();
                if (record is null) break;
                count++;
                if (record.TimeCreated is { } t) markers.Add(t);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* System log unreadable - no boot markers, #133 degrades to "no boot data" */ }

        markers.Sort();
        return markers;
    }

    /// <summary>Splits <paramref name="rows"/> by whether each falls within <paramref
    /// name="bootWindow"/> (default 120 seconds per #133's spec) of a preceding boot marker, then
    /// groups by provider - a provider with BootCount &gt; 0 and SteadyStateCount == 0 only ever
    /// fails right after boot (driver load order / startup service suspects); the reverse points at
    /// hardware or a running app instead.</summary>
    public BootErrorProfileResult ComputeBootProfile(IEnumerable<EventRecordRow> rows, IReadOnlyList<DateTime> bootMarkers, TimeSpan bootWindow)
    {
        var byProvider = new Dictionary<string, (int Boot, int Steady)>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            bool isBoot = bootMarkers.Any(b => row.TimeCreated >= b && row.TimeCreated <= b + bootWindow);
            var current = byProvider.GetValueOrDefault(row.ProviderName);
            byProvider[row.ProviderName] = isBoot ? (current.Boot + 1, current.Steady) : (current.Boot, current.Steady + 1);
        }

        var providerRows = byProvider
            .Select(kv => new BootErrorProfileRow
            {
                Provider = kv.Key,
                BootCount = kv.Value.Boot,
                SteadyStateCount = kv.Value.Steady,
                IsBootOnly = kv.Value.Boot > 0 && kv.Value.Steady == 0,
            })
            .Where(r => r.BootCount > 0) // only providers that showed up in a boot window at all are interesting here
            .OrderByDescending(r => r.BootCount)
            .ToList();

        return new BootErrorProfileResult { Providers = providerRows, BootMarkersFound = bootMarkers.Count };
    }

    // ==== #134: "since it was working" diff ====

    /// <summary>Splits <paramref name="rows"/> at <paramref name="cutoff"/> and diffs the distinct
    /// (provider, eventId) signatures on each side - signatures present only after the cutoff
    /// ("new since it broke") and signatures present only before it ("stopped happening"). Turns a
    /// vague "it broke sometime last month" into a concrete list of what actually changed.</summary>
    public SinceWorkingDiffResult DiffSinceDate(IReadOnlyList<EventRecordRow> rows, DateTime cutoff)
    {
        var before = rows.Where(r => r.TimeCreated < cutoff).ToList();
        var after = rows.Where(r => r.TimeCreated >= cutoff).ToList();

        var beforeSignatures = before.Select(r => (r.ProviderName, r.EventId)).ToHashSet();
        var afterSignatures = after.Select(r => (r.ProviderName, r.EventId)).ToHashSet();

        var newSignatures = afterSignatures.Except(beforeSignatures)
            .Select(sig =>
            {
                var first = after.Where(r => r.ProviderName == sig.ProviderName && r.EventId == sig.EventId).MinBy(r => r.TimeCreated)!;
                return new EventSignatureDiffRow { Provider = sig.ProviderName, EventId = sig.EventId, Timestamp = first.TimeCreated, SampleMessage = first.Message };
            })
            .OrderByDescending(s => s.Timestamp)
            .ToList();

        var stoppedSignatures = beforeSignatures.Except(afterSignatures)
            .Select(sig =>
            {
                var last = before.Where(r => r.ProviderName == sig.ProviderName && r.EventId == sig.EventId).MaxBy(r => r.TimeCreated)!;
                return new EventSignatureDiffRow { Provider = sig.ProviderName, EventId = sig.EventId, Timestamp = last.TimeCreated, SampleMessage = last.Message };
            })
            .OrderByDescending(s => s.Timestamp)
            .ToList();

        return new SinceWorkingDiffResult { NewSignatures = newSignatures, StoppedSignatures = stoppedSignatures };
    }
}
