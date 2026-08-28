using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #401: persistent, cross-app-restart per-process memory/handle/thread history, keyed by image
/// name rather than PID (a PID is meaningless across a restart of this app; several currently-
/// running processes that share one name are summed into a single per-tick sample, the same
/// "group by name" idea ProcessMonitorService.ComputeDuplicateInstances already uses). This
/// extends - doesn't replace - the existing in-memory, per-PID, working-set-only
/// ComputeLeakSuspect heuristic in ProcessMonitorService, which is lost the instant a process
/// exits; this history is longer, covers more fields, and survives Task Manager Plus itself
/// being closed and reopened.
///
/// RecordSample is called once per Processes-tab tick, off the UI thread (inside
/// ProcessesViewModel's existing Task.Run around ProcessMonitorService.Sample()) - it appends
/// this tick's aggregate sample, runs a least-squares regression over private bytes/handle
/// count/thread count (#402/#403/#405), and writes the results straight onto the ProcessRow
/// instances passed in. That mirrors how every other per-tick computed field in this app
/// (IsLeakSuspect, SpawnGroupSize, ...) gets set on a freshly-built row before MergeInto copies
/// it onto the UI-bound instance - safe here for the same reason: the rows passed in aren't yet
/// attached to any UI-bound collection.
///
/// Flushes to disk on a slow, size-bounded cadence rather than every tick - "record often,
/// persist occasionally" is the same split LoggingService's 100MB rotation exists for, just
/// time-based instead of size-based here.
/// </summary>
public sealed class ProcessHistoryService
{
    // ~this many ticks of history per name - long enough for a meaningful slope at the default
    // 1s poll interval (720 samples is a couple of hours), short enough that a system with many
    // distinct executables still keeps a reasonably small JSON file.
    private const int MaxSamplesPerName = 720;

    // Caps how many distinct image names get a JSON entry - an always-on system slowly
    // accumulates history for every executable it has ever seen, including one-off tools
    // launched from a temp folder; the least-recently-seen names are dropped first once this is
    // exceeded, same idea as MaxSamplesPerName bounding a single record.
    private const int MaxTrackedNames = 300;

    private const int SparklineLength = 20;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    // Tuned thresholds for the "quick flag, not a verdict" regression-based flags below - see
    // ApplyComputedFields' remarks for why these particular numbers.
    private const double HandleLeakSlopePerHour = 50.0;
    private const double HandleLeakMinRSquared = 0.6;
    private const double HandleLeakFlatPrivateBytesMbPerHour = 5.0;
    private const double ThreadRunawaySlopePerHour = 15.0;
    private const double ThreadRunawayMinRSquared = 0.6;
    private const int MinSamplesForRegression = 6;

    private readonly Dictionary<string, ProcessHistoryRecord> _byName;
    private readonly object _gate = new();
    private DateTime _lastFlushUtc = DateTime.UtcNow;
    private bool _dirty;

    public ProcessHistoryService()
    {
        _byName = Load();
    }

    /// <summary>
    /// Records this tick's per-name aggregate sample and writes MemorySparkline/
    /// LeakSlopeMbPerHour/LeakRSquared/IsHandleLeakSuspect/IsThreadRunawaySuspect onto every row
    /// in <paramref name="rows"/> - safe to call from a background thread, see remarks above.
    /// </summary>
    public void RecordSample(List<ProcessRow> rows)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (var group in rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(group.Key)) continue;

                if (!_byName.TryGetValue(group.Key, out var record))
                {
                    record = new ProcessHistoryRecord { ImageName = group.Key };
                    _byName[group.Key] = record;
                }
                record.LastSeenUtc = now;

                record.Samples.Add(new ProcessHistorySample
                {
                    TimestampUtc = now,
                    WorkingSetBytes = group.Sum(r => r.MemoryBytes),
                    PrivateBytes = group.Sum(r => r.PrivateBytes),
                    HandleCount = group.Sum(r => r.HandleCount),
                    GdiHandleCount = group.Sum(r => r.GdiHandleCount),
                    UserHandleCount = group.Sum(r => r.UserHandleCount),
                    ThreadCount = group.Sum(r => r.ThreadCount),
                });
                while (record.Samples.Count > MaxSamplesPerName) record.Samples.RemoveAt(0);

                ApplyComputedFields(group, record);
                _dirty = true;
            }

            PruneStaleNames();
        }

        if (now - _lastFlushUtc >= FlushInterval)
        {
            _lastFlushUtc = now;
            Flush();
        }
    }

    /// <summary>The recorded sample history for one image name, oldest first - used by the
    /// leak-investigation report section (#407). Empty for a name never seen.</summary>
    public IReadOnlyList<ProcessHistorySample> GetHistory(string imageName)
    {
        lock (_gate)
        {
            return _byName.TryGetValue(imageName, out var record) ? record.Samples.ToList() : Array.Empty<ProcessHistorySample>();
        }
    }

    /// <summary>#407: the top <paramref name="count"/> image names by private-bytes growth rate,
    /// with enough recorded history to fit a meaningful regression - the data source for the
    /// diagnostic report's "Memory leak evidence" section.</summary>
    public IReadOnlyList<ProcessHistorySummary> GetTopGrowthSummaries(int count)
    {
        lock (_gate)
        {
            return _byName.Values
                .Where(r => r.Samples.Count >= MinSamplesForRegression)
                .Select(BuildSummary)
                .OrderByDescending(s => s.PrivateBytesSlopeMbPerHour)
                .Take(count)
                .ToList();
        }
    }

    private static ProcessHistorySummary BuildSummary(ProcessHistoryRecord record)
    {
        var (pSlope, pR2) = Regress(record.Samples, s => s.PrivateBytes / (1024.0 * 1024.0));
        var (hSlope, hR2) = Regress(record.Samples, s => s.HandleCount);
        var (tSlope, tR2) = Regress(record.Samples, s => s.ThreadCount);

        return new ProcessHistorySummary
        {
            ImageName = record.ImageName,
            PrivateBytesSlopeMbPerHour = Math.Round(pSlope, 2),
            PrivateBytesRSquared = Math.Round(pR2, 3),
            HandleSlopePerHour = Math.Round(hSlope, 2),
            HandleRSquared = Math.Round(hR2, 3),
            ThreadSlopePerHour = Math.Round(tSlope, 2),
            ThreadRSquared = Math.Round(tR2, 3),
            SampleCount = record.Samples.Count,
            FirstSampleUtc = record.Samples[0].TimestampUtc,
            LastSampleUtc = record.Samples[^1].TimestampUtc,
        };
    }

    /// <summary>
    /// #402: private-bytes slope (MB/hour) and R² become sortable Processes columns - a
    /// magnitude and a confidence, distinguishing a steady climb from a sawtooth allocate/free
    /// pattern, on top of the existing boolean IsLeakSuspect dot.
    ///
    /// #403: a process is flagged as a handle leak when its handle count climbs steadily
    /// (a positive slope with a high R² - "steadily", not just "sometimes higher") while its
    /// private bytes stay essentially flat - the classic kernel-object-leak signature, distinct
    /// from an ordinary memory leak where both climb together.
    ///
    /// #405: a process is flagged as a thread-count runaway on the same "steady, not noisy"
    /// shape applied to thread count instead of handle count - a thread-pool leak or unbounded
    /// worker creation, not a process that simply spun up a few extra threads once and plateaued
    /// (a plateau flattens the fit, so R² drops below the threshold once growth stops).
    ///
    /// All three are heuristic pattern-matches on otherwise-ambiguous data ("quick flag, not a
    /// verdict", the same tier as ComputeLeakSuspect/ComputeSpawnGroups elsewhere in this app) -
    /// a process that legitimately needs more handles/threads over time (e.g. opening more
    /// files, or scaling up a thread pool under real load) can also match.
    /// </summary>
    private static void ApplyComputedFields(IEnumerable<ProcessRow> group, ProcessHistoryRecord record)
    {
        var samples = record.Samples;

        double[] sparkline = samples.Count <= SparklineLength
            ? samples.Select(s => (double)s.PrivateBytes).ToArray()
            : samples.Skip(samples.Count - SparklineLength).Select(s => (double)s.PrivateBytes).ToArray();

        var (privateSlopePerHour, privateR2) = Regress(samples, s => s.PrivateBytes / (1024.0 * 1024.0));
        var (handleSlopePerHour, handleR2) = Regress(samples, s => s.HandleCount);
        var (threadSlopePerHour, threadR2) = Regress(samples, s => s.ThreadCount);

        bool enoughData = samples.Count >= MinSamplesForRegression;

        bool isHandleLeak = enoughData &&
            handleSlopePerHour >= HandleLeakSlopePerHour && handleR2 >= HandleLeakMinRSquared &&
            Math.Abs(privateSlopePerHour) < HandleLeakFlatPrivateBytesMbPerHour;

        bool isThreadRunaway = enoughData &&
            threadSlopePerHour >= ThreadRunawaySlopePerHour && threadR2 >= ThreadRunawayMinRSquared;

        foreach (var row in group)
        {
            row.MemorySparkline = sparkline;
            row.LeakSlopeMbPerHour = Math.Round(privateSlopePerHour, 2);
            row.LeakRSquared = Math.Round(privateR2, 3);
            row.IsHandleLeakSuspect = isHandleLeak;
            row.IsThreadRunawaySuspect = isThreadRunaway;
        }
    }

    /// <summary>
    /// Least-squares slope (per hour) and R² of <paramref name="selector"/> over elapsed time -
    /// the same standard formula behind Excel's SLOPE/RSQ. Fewer than two samples, or samples
    /// that don't actually span any measurable time, returns (0, 0) rather than dividing by
    /// zero. A perfectly flat series (zero variance in the selected value) reports R²=1 (the
    /// zero slope explains it perfectly), not the 0/0 that plain correlation math would give.
    /// </summary>
    private static (double SlopePerHour, double RSquared) Regress(IReadOnlyList<ProcessHistorySample> samples, Func<ProcessHistorySample, double> selector)
    {
        int n = samples.Count;
        if (n < 2) return (0, 0);

        var t0 = samples[0].TimestampUtc;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0, sumYY = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (samples[i].TimestampUtc - t0).TotalHours;
            double y = selector(samples[i]);
            sumX += x; sumY += y; sumXY += x * y; sumXX += x * x; sumYY += y * y;
        }

        double denomSlope = n * sumXX - sumX * sumX;
        if (denomSlope <= 1e-9) return (0, 0);

        double slope = (n * sumXY - sumX * sumY) / denomSlope;

        double denomCorr = Math.Sqrt(Math.Max(0, (n * sumXX - sumX * sumX) * (n * sumYY - sumY * sumY)));
        double r2 = denomCorr <= 1e-9
            ? (Math.Abs(slope) < 1e-9 ? 1.0 : 0.0)
            : Math.Pow((n * sumXY - sumX * sumY) / denomCorr, 2);

        return (slope, Math.Clamp(r2, 0, 1));
    }

    private void PruneStaleNames()
    {
        if (_byName.Count <= MaxTrackedNames) return;

        var toRemove = _byName.Values
            .OrderBy(r => r.LastSeenUtc)
            .Take(_byName.Count - MaxTrackedNames)
            .Select(r => r.ImageName)
            .ToList();
        foreach (var name in toRemove) _byName.Remove(name);
    }

    /// <summary>Writes the current in-memory history to disk (#401 - best-effort, same silent-
    /// fail-to-defaults shape every settings file in this app already uses). Safe to call from
    /// any thread; also called once from MainViewModel.Dispose so a session shorter than
    /// FlushInterval still persists something on a clean exit.</summary>
    public void Flush()
    {
        List<ProcessHistoryRecord> snapshot;
        lock (_gate)
        {
            if (!_dirty) return;
            snapshot = _byName.Values.ToList();
            _dirty = false;
        }

        try
        {
            var path = AppPaths.GetPath("process-history.json");
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(snapshot);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best-effort - a failed write just means this session's trend data doesn't survive
            // the next restart; the in-memory history for the running session is unaffected.
        }
    }

    private static Dictionary<string, ProcessHistoryRecord> Load()
    {
        try
        {
            var path = AppPaths.GetPath("process-history.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var records = JsonSerializer.Deserialize<List<ProcessHistoryRecord>>(json);
                if (records is not null)
                {
                    return records
                        .Where(r => !string.IsNullOrEmpty(r.ImageName))
                        .ToDictionary(r => r.ImageName, r => r, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            // Corrupt or unreadable file - start with an empty history rather than failing
            // startup, same as every other settings/history file in this app.
        }
        return new Dictionary<string, ProcessHistoryRecord>(StringComparer.OrdinalIgnoreCase);
    }
}
