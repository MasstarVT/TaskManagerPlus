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
///
/// #1078: persistence is decimate-and-append, not rewrite-the-world. Each 30s flush appends one
/// small JSON line to process-history.jsonl carrying only the newest sample per dirty name (a
/// natural decimation to FlushInterval resolution - the cross-restart trend math regresses over
/// real timestamps, so it never needed 1s-resolution history on disk); the journal is compacted
/// into a single decimated snapshot line only when it outgrows CompactThresholdBytes. The old
/// shape serialized and File.WriteAllText'd the whole ~15-25MB store every 30s (2-3 GB/hour of
/// writes for a sparkline and three slopes). Loading is also off-thread now: the ctor spawns a
/// Task.Run that parses the journal (or the legacy process-history.json, still read so an
/// upgrade keeps its history) and merges it under the lock - construction itself does no I/O,
/// which is what let MainViewModel's field initializer run on the UI thread safely (the
/// BitLockerService startup-stall lesson).
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

    // #1078: once the append-only journal grows past this, the next flush rewrites it as one
    // decimated snapshot line instead of appending - the only time the whole store is written.
    // At the observed delta rate (a few KB per 30s flush) this fires every hour or two.
    private const long CompactThresholdBytes = 4 * 1024 * 1024;

    private readonly Dictionary<string, ProcessHistoryRecord> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    // #1078: file-writer gate, always taken OUTSIDE _gate (Flush and the load-merge both order
    // _flushGate -> _gate, never the reverse) - serializes journal appends/compactions against
    // each other and against the background load's merge, without holding the data lock during I/O.
    private readonly object _flushGate = new();
    private long _journalBytes;

    // Newest sample timestamp already persisted per image name - what BuildDeltaLocked diffs
    // against so a flush appends each sample at most once. Pruned alongside _byName.
    private readonly Dictionary<string, DateTime> _lastPersistedUtc = new(StringComparer.OrdinalIgnoreCase);

    private DateTime _lastFlushUtc = DateTime.UtcNow;
    private bool _dirty;

    /// <summary>One line of process-history.jsonl: a full-store snapshot (written at compaction)
    /// or a per-flush delta of newest-sample-per-name records. Load starts from the last snapshot
    /// line and replays the deltas after it.</summary>
    private sealed class JournalLine
    {
        public string Kind { get; set; } = "delta";
        public List<ProcessHistoryRecord> Records { get; set; } = new();
    }

    public ProcessHistoryService()
    {
        // #1078: no synchronous I/O in the ctor - MainViewModel field-initializes this on the UI
        // thread during window construction. The load parses off-thread and merges under the lock,
        // so samples recorded before the merge lands are kept (loaded history is older and is
        // prepended beneath them).
        _ = Task.Run(LoadAndMerge);
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
        foreach (var name in toRemove)
        {
            _byName.Remove(name);
            _lastPersistedUtc.Remove(name);
        }
    }

    private static string JournalPath => AppPaths.GetPath("process-history.jsonl");
    private static string LegacyStorePath => AppPaths.GetPath("process-history.json");

    /// <summary>Persists what changed since the last flush (#401/#1078 - best-effort, same
    /// silent-fail-to-defaults shape every settings file in this app already uses). Appends one
    /// small newest-sample-per-name delta line; only compaction (journal past
    /// CompactThresholdBytes) rewrites the file, as a single decimated snapshot line. Safe to
    /// call from any thread; also called once from MainViewModel.Dispose so a session shorter
    /// than FlushInterval still persists something on a clean exit.</summary>
    public void Flush()
    {
        lock (_flushGate)
        {
            List<ProcessHistoryRecord> delta;
            lock (_gate)
            {
                if (!_dirty) return;
                delta = BuildDeltaLocked();
                _dirty = false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);

                if (_journalBytes >= CompactThresholdBytes)
                {
                    List<ProcessHistoryRecord> snapshot;
                    lock (_gate) snapshot = BuildDecimatedSnapshotLocked();
                    var line = JsonSerializer.Serialize(new JournalLine { Kind = "snapshot", Records = snapshot }) + Environment.NewLine;

                    // Temp-then-replace so a crash mid-compaction leaves the old journal intact
                    // rather than a truncated one.
                    string temp = JournalPath + ".tmp";
                    File.WriteAllText(temp, line);
                    File.Move(temp, JournalPath, overwrite: true);
                    _journalBytes = line.Length;
                }
                else if (delta.Count > 0)
                {
                    var line = JsonSerializer.Serialize(new JournalLine { Kind = "delta", Records = delta }) + Environment.NewLine;
                    File.AppendAllText(JournalPath, line);
                    _journalBytes += line.Length;
                }
            }
            catch
            {
                // Best-effort - a failed write just means this session's trend data doesn't
                // survive the next restart; the in-memory history for the running session is
                // unaffected.
            }
        }
    }

    /// <summary>Newest not-yet-persisted sample per image name - one sample per name per flush,
    /// which is exactly the FlushInterval-resolution decimation the on-disk store keeps.</summary>
    private List<ProcessHistoryRecord> BuildDeltaLocked()
    {
        var delta = new List<ProcessHistoryRecord>();
        foreach (var record in _byName.Values)
        {
            if (record.Samples.Count == 0) continue;
            var newest = record.Samples[^1];
            if (_lastPersistedUtc.TryGetValue(record.ImageName, out var last) && newest.TimestampUtc <= last) continue;

            delta.Add(new ProcessHistoryRecord
            {
                ImageName = record.ImageName,
                LastSeenUtc = record.LastSeenUtc,
                Samples = new List<ProcessHistorySample> { newest },
            });
            _lastPersistedUtc[record.ImageName] = newest.TimestampUtc;
        }
        return delta;
    }

    /// <summary>The full store, decimated per name to FlushInterval spacing (always keeping the
    /// newest sample) - the same resolution the delta lines persist at, so compaction never
    /// bloats the file back up with this session's 1s-resolution in-memory samples.</summary>
    private List<ProcessHistoryRecord> BuildDecimatedSnapshotLocked()
    {
        var snapshot = new List<ProcessHistoryRecord>(_byName.Count);
        foreach (var record in _byName.Values)
        {
            var kept = new List<ProcessHistorySample>();
            DateTime lastKept = DateTime.MinValue;
            for (int i = 0; i < record.Samples.Count; i++)
            {
                var s = record.Samples[i];
                if (i == record.Samples.Count - 1 || s.TimestampUtc - lastKept >= FlushInterval)
                {
                    kept.Add(s);
                    lastKept = s.TimestampUtc;
                }
            }
            snapshot.Add(new ProcessHistoryRecord { ImageName = record.ImageName, LastSeenUtc = record.LastSeenUtc, Samples = kept });
        }
        return snapshot;
    }

    /// <summary>#1078: the ctor's Task.Run body - parses the journal (or the legacy single-file
    /// store, so an upgrade keeps its history) off-thread, then merges beneath whatever this
    /// session has already recorded. Any failure degrades to an empty loaded set rather than
    /// failing startup, same as every other settings/history file in this app.</summary>
    private void LoadAndMerge()
    {
        Dictionary<string, ProcessHistoryRecord> loaded;
        long journalBytes = 0;
        bool forceCompactOnFirstFlush = false;

        try
        {
            if (File.Exists(JournalPath))
            {
                loaded = LoadFromJournal(out journalBytes);
            }
            else if (File.Exists(LegacyStorePath))
            {
                loaded = LoadFromLegacyStore();
                // The legacy file is never written again - force the first flush to compact, so
                // the journal starts with a snapshot carrying this history forward.
                forceCompactOnFirstFlush = loaded.Count > 0;
            }
            else
            {
                loaded = new Dictionary<string, ProcessHistoryRecord>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            loaded = new Dictionary<string, ProcessHistoryRecord>(StringComparer.OrdinalIgnoreCase);
        }

        lock (_flushGate)
        {
            lock (_gate)
            {
                foreach (var (name, record) in loaded)
                {
                    if (record.Samples.Count == 0) continue;

                    if (_byName.TryGetValue(name, out var existing))
                    {
                        // The session started recording this name before the load landed - the
                        // loaded samples are from a previous session (older), so they go first.
                        existing.Samples.InsertRange(0, record.Samples);
                        while (existing.Samples.Count > MaxSamplesPerName) existing.Samples.RemoveAt(0);
                    }
                    else
                    {
                        while (record.Samples.Count > MaxSamplesPerName) record.Samples.RemoveAt(0);
                        _byName[name] = record;
                    }

                    // Everything just loaded is already on disk - don't re-append it, unless a
                    // flush this session already persisted something newer for the name.
                    var newest = record.Samples[^1].TimestampUtc;
                    if (!_lastPersistedUtc.TryGetValue(name, out var t) || newest > t)
                        _lastPersistedUtc[name] = newest;
                }
                PruneStaleNames();
                if (forceCompactOnFirstFlush) _dirty = true;
            }
            _journalBytes = forceCompactOnFirstFlush ? CompactThresholdBytes : Math.Max(_journalBytes, journalBytes);
        }
    }

    private static Dictionary<string, ProcessHistoryRecord> LoadFromJournal(out long journalBytes)
    {
        var byName = new Dictionary<string, ProcessHistoryRecord>(StringComparer.OrdinalIgnoreCase);
        var lines = File.ReadAllLines(JournalPath);
        journalBytes = new FileInfo(JournalPath).Length;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            JournalLine? line;
            try { line = JsonSerializer.Deserialize<JournalLine>(raw); }
            catch { continue; } // one corrupt line never takes the rest of the journal down
            if (line is null) continue;

            // A snapshot line supersedes everything before it.
            if (string.Equals(line.Kind, "snapshot", StringComparison.OrdinalIgnoreCase)) byName.Clear();

            foreach (var record in line.Records)
            {
                if (string.IsNullOrEmpty(record.ImageName)) continue;
                if (byName.TryGetValue(record.ImageName, out var existing))
                {
                    existing.Samples.AddRange(record.Samples);
                    while (existing.Samples.Count > MaxSamplesPerName) existing.Samples.RemoveAt(0);
                    if (record.LastSeenUtc > existing.LastSeenUtc) existing.LastSeenUtc = record.LastSeenUtc;
                }
                else
                {
                    byName[record.ImageName] = record;
                }
            }
        }
        return byName;
    }

    /// <summary>The pre-#1078 single-file store, read (never written) so an upgrade doesn't lose
    /// the history it had accumulated.</summary>
    private static Dictionary<string, ProcessHistoryRecord> LoadFromLegacyStore()
    {
        var json = File.ReadAllText(LegacyStorePath);
        var records = JsonSerializer.Deserialize<List<ProcessHistoryRecord>>(json);
        return records is null
            ? new Dictionary<string, ProcessHistoryRecord>(StringComparer.OrdinalIgnoreCase)
            : records
                .Where(r => !string.IsNullOrEmpty(r.ImageName))
                .ToDictionary(r => r.ImageName, r => r, StringComparer.OrdinalIgnoreCase);
    }
}
