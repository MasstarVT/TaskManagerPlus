using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Reads the System and Application event logs for crash/stability diagnostics (#1/#3/#4/#5/#6/#8) -
/// the same source Windows' own Reliability Monitor and Event Viewer read from, just filtered down
/// to what's actually actionable for "why did my PC crash/hang/reboot". Every read is wrapped to
/// degrade to "nothing found" rather than throwing: a locked-down policy, a cleared log, or a
/// provider whose message file isn't registered on this machine are all real, expected conditions,
/// not bugs.
/// </summary>
public sealed class EventLogService
{
    private const int LookbackDays = 30;
    private const int MaxEventsPerLog = 60;

    // Kernel-Power 41 = "The system has rebooted without cleanly shutting down first" - the
    // classic signal WhoCrashed/Reliability Monitor use for "this was a real crash". EventLog
    // 6008 is the older, plain-text version of the same event.
    private const int KernelPowerEventId = 41;
    private const int LegacyUncleanShutdownEventId = 6008;
    private const int TdrEventId = 4101;

    // Windows Error Reporting's own "a Blue Screen just happened" summary entry (Application log).
    private const int BlueScreenEventId = 1001;

    private static readonly Regex FaultingModuleRegex = new(@"Faulting module name:\s*([^,\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public StabilitySnapshot Query()
    {
        var events = new List<StabilityEvent>();
        ReadLog("System", events);
        ReadLog("Application", events);
        events = events.OrderByDescending(e => e.TimeCreated).Take(MaxEventsPerLog * 2).ToList();

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var approxLastBoot = DateTime.Now - uptime;

        var shutdownEvents = events
            .Where(e => e.LogName == "System" && e.EventId is KernelPowerEventId or LegacyUncleanShutdownEventId)
            .OrderByDescending(e => e.TimeCreated)
            .ToList();
        var mostRecentShutdownEvent = shutdownEvents.FirstOrDefault();
        bool wasUnexpected = mostRecentShutdownEvent is not null &&
            Math.Abs((mostRecentShutdownEvent.TimeCreated - approxLastBoot).TotalMinutes) < 5;

        var tdrEvents = events.Where(e => e.EventId == TdrEventId).OrderByDescending(e => e.TimeCreated).ToList();

        var crashLikeIds = new HashSet<int> { KernelPowerEventId, LegacyUncleanShutdownEventId, BlueScreenEventId };
        var lastCrash = events.Where(e => crashLikeIds.Contains(e.EventId)).OrderByDescending(e => e.TimeCreated).FirstOrDefault();

        var (lowMemCount, lowMemLast) = ReadLowMemoryEvents();

        return new StabilitySnapshot
        {
            RecentEvents = events.Take(MaxEventsPerLog).ToList(),
            WasLastShutdownUnexpected = wasUnexpected,
            LastUnexpectedShutdown = mostRecentShutdownEvent?.TimeCreated,
            TdrEventCount = tdrEvents.Count,
            LastTdrEvent = tdrEvents.FirstOrDefault()?.TimeCreated,
            LastCrashTime = lastCrash?.TimeCreated,
            Minidumps = ReadMinidumps(shutdownEvents),
            DailyCounts = BuildDailyCounts(events),
            LowMemoryEventCount = lowMemCount,
            LastLowMemoryEvent = lowMemLast,
        };
    }

    // Round 8 #40: low-memory resource-exhaustion events are logged by a dedicated Windows
    // component at Warning level, not Critical/Error - outside the Level=1|2 filter the main scan
    // above uses - so this is a second, separately targeted query for just this one provider,
    // the same shape ReadServiceStartDurations below already uses for a different provider/event.
    private const string ResourceExhaustionProvider = "Microsoft-Windows-Resource-Exhaustion-Detector";

    private static (int Count, DateTime? Last) ReadLowMemoryEvents()
    {
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{ResourceExhaustionProvider}'] and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            DateTime? last = null;
            const int maxEvents = 200;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    last ??= record.TimeCreated;
                }
            }
            return (count, last);
        }
        catch
        {
            // Provider/log unavailable - degrade to "none found", same as every other event-log
            // read in this service.
            return (0, null);
        }
    }

    /// <summary>#1: Reliability History - buckets the same events already read into one count per
    /// calendar day across the full lookback window, zero-filled for days with no Critical/Error
    /// entries at all (Reliability Monitor's own chart shows the flat baseline too, not just spikes).</summary>
    private static List<DailyEventCount> BuildDailyCounts(List<StabilityEvent> events)
    {
        var counts = events
            .GroupBy(e => e.TimeCreated.Date)
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new List<DailyEventCount>();
        var today = DateTime.Now.Date;
        for (int i = LookbackDays - 1; i >= 0; i--)
        {
            var day = today.AddDays(-i);
            result.Add(new DailyEventCount { Date = day, Count = counts.TryGetValue(day, out var c) ? c : 0 });
        }
        return result;
    }

    private static void ReadLog(string logName, List<StabilityEvent> into)
    {
        try
        {
            // Level 1 = Critical, Level 2 = Error - Warning/Information would dominate the list
            // with noise that isn't actionable for a crash/stability investigation.
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery(logName, PathType.LogName,
                $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxEventsPerLog && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    into.Add(new StabilityEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        LogName = logName,
                        ProviderName = record.ProviderName ?? string.Empty,
                        EventId = record.Id,
                        Level = record.LevelDisplayName ?? string.Empty,
                        Message = Truncate(message, 300),
                        FaultingModule = ExtractFaultingModule(message),
                        BugcheckCode = record.Id == KernelPowerEventId ? ExtractBugcheckCode(record) : null,
                    });
                }
            }
        }
        catch
        {
            // Log unavailable/access denied/doesn't exist - contribute nothing from this log.
        }
    }

    private static string? ExtractFaultingModule(string message)
    {
        var match = FaultingModuleRegex.Match(message);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Event 41's insertion strings put the bugcheck code first (as a hex-formatted number, e.g.
    /// "0x0000009f") on every Windows version this app targets - not a documented, versioned
    /// contract, so this is wrapped to return null (shown as "Unknown") rather than a
    /// misleading value if the layout ever changes.
    /// </summary>
    private static string? ExtractBugcheckCode(EventRecord record)
    {
        try
        {
            if (record.Properties.Count == 0) return null;
            var raw = record.Properties[0].Value;
            ulong code = Convert.ToUInt64(raw);
            return code == 0 ? null : $"0x{code:X8}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Correlates each minidump file's timestamp with the nearest Kernel-Power 41 event
    /// (within 10 minutes) to recover its bugcheck code (#3) - parsing the bugcheck code directly
    /// out of the .dmp binary format would need a full MINIDUMP-stream reader, a much larger and
    /// more fragile undertaking than reusing the event log entry already read for the shutdown
    /// banner above.</summary>
    private static List<MinidumpInfo> ReadMinidumps(List<StabilityEvent> shutdownEvents)
    {
        var dumps = new List<MinidumpInfo>();
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
            if (!Directory.Exists(dir)) return dumps;

            var bugchecks = shutdownEvents.Where(e => e.EventId == KernelPowerEventId).ToList();

            foreach (var file in Directory.GetFiles(dir, "*.dmp"))
            {
                var info = new FileInfo(file);
                var nearest = bugchecks
                    .Where(e => Math.Abs((e.TimeCreated - info.LastWriteTime).TotalMinutes) < 10)
                    .OrderBy(e => Math.Abs((e.TimeCreated - info.LastWriteTime).TotalMinutes))
                    .FirstOrDefault();

                dumps.Add(new MinidumpInfo
                {
                    FileName = info.Name,
                    Timestamp = info.LastWriteTime,
                    BugcheckCode = nearest?.BugcheckCode,
                });
            }
        }
        catch
        {
            // Folder missing or access denied - an empty list just means nothing to show.
        }
        return dumps.OrderByDescending(d => d.Timestamp).ToList();
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "…";

    // Round 7 #13: how long a service "took to start", mined from the System log's own Service
    // Control Manager event 7036 ("service entered the running/stopped state") - the only
    // service-lifecycle event Windows logs by default. There is no explicit "start requested at"
    // timestamp in default logging (that needs Verbose SCM ETW tracing, a much heavier ask than
    // this app's other event-log reads), so this approximates duration as the time between a
    // service's most recent "stopped" 7036 entry and the following "running" 7036 entry for the
    // same service - a real, if approximate, measurement of the same shape EventLogService's
    // WasLastShutdownUnexpected correlation already uses elsewhere. A stopped-to-running gap wider
    // than StartDurationCeiling is treated as "the service just sat stopped for a while, then
    // happened to start" rather than a real start latency, and discarded rather than reported as a
    // wildly inflated duration.
    private static readonly TimeSpan StartDurationCeiling = TimeSpan.FromMinutes(3);

    public List<ServiceStartDuration> ReadServiceStartDurations()
    {
        var byService = new Dictionary<string, List<(DateTime Time, bool Running)>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Service Control Manager'] and (EventID=7036) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]");

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 4000; // generous - the SCM can log thousands of these over 30 days on a busy machine
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    try
                    {
                        if (record.Properties.Count < 2) continue;
                        string? name = record.Properties[0].Value as string;
                        string? state = record.Properties[1].Value as string;
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(state) || record.TimeCreated is null) continue;

                        bool running = state.Equals("running", StringComparison.OrdinalIgnoreCase);
                        if (!running && !state.Equals("stopped", StringComparison.OrdinalIgnoreCase)) continue;

                        if (!byService.TryGetValue(name, out var list))
                            byService[name] = list = new List<(DateTime, bool)>();
                        list.Add((record.TimeCreated.Value, running));
                    }
                    catch { /* one malformed entry shouldn't stop the rest of the scan */ }
                }
            }
        }
        catch
        {
            // Log unavailable/access denied - degrade to "no start-duration data".
        }

        var result = new List<ServiceStartDuration>();
        foreach (var (name, transitions) in byService)
        {
            var ordered = transitions.OrderBy(t => t.Time).ToList();
            var durations = new List<double>();
            for (int i = 1; i < ordered.Count; i++)
            {
                if (!ordered[i].Running || ordered[i - 1].Running) continue; // only a stopped -> running pair
                var gap = ordered[i].Time - ordered[i - 1].Time;
                if (gap > TimeSpan.Zero && gap <= StartDurationCeiling)
                    durations.Add(gap.TotalMilliseconds);
            }
            if (durations.Count == 0) continue;

            result.Add(new ServiceStartDuration
            {
                ServiceName = name,
                LastStartDurationMs = durations[^1],
                AvgStartDurationMs = durations.Average(),
                SampleCount = durations.Count,
            });
        }
        return result;
    }

    // #749/#750/#751: the Service Control Manager failure/crash event IDs this domain's design
    // note asks for one shared scan to back three different views of - a per-service timeline
    // (#749), a rolling-window crash-loop count (#750), and a dependency-failure root-cause walk
    // (#751, done in ServicesViewModel once every row's events are assigned).
    private static readonly int[] ScmFailureEventIds = { 7000, 7001, 7009, 7011, 7022, 7023, 7024, 7031, 7034, 7043 };

    // Every one of these message templates names the affected service as "The X service ..." -
    // except 7009/7011, whose template puts a timeout value first instead. Matched against the
    // fully rendered message (FormatDescription()) rather than a Properties[] index, since there's
    // no single index that's stable across this whole event-ID set - see ServiceScmEvent's remarks.
    private static readonly Dictionary<int, Regex> ScmServiceNamePatterns = new()
    {
        [7000] = new Regex(@"^The (?<svc>.+?) service failed to start", RegexOptions.IgnoreCase),
        [7001] = new Regex(@"^The (?<svc>.+?) service depends on", RegexOptions.IgnoreCase),
        [7009] = new Regex(@"waiting for the (?<svc>.+?) service to connect", RegexOptions.IgnoreCase),
        [7011] = new Regex(@"transaction response from the (?<svc>.+?) service", RegexOptions.IgnoreCase),
        [7022] = new Regex(@"^The (?<svc>.+?) service hung on starting", RegexOptions.IgnoreCase),
        [7023] = new Regex(@"^The (?<svc>.+?) service terminated with the following error", RegexOptions.IgnoreCase),
        [7024] = new Regex(@"^The (?<svc>.+?) service terminated with service-specific error", RegexOptions.IgnoreCase),
        [7031] = new Regex(@"^The (?<svc>.+?) service terminated unexpectedly", RegexOptions.IgnoreCase),
        [7034] = new Regex(@"^The (?<svc>.+?) service terminated unexpectedly", RegexOptions.IgnoreCase),
        [7043] = new Regex(@"^The (?<svc>.+?) service did not shut down properly", RegexOptions.IgnoreCase),
    };

    // #750: SCM's own "It has done this N time(s)" restart count, present in both 7031 and 7034's
    // message text.
    private static readonly Regex RestartCountRegex = new(@"done this (?<n>\d+) time", RegexOptions.IgnoreCase);

    /// <summary>
    /// #749/#750/#751: scans the System log's Service Control Manager provider for the failure/
    /// crash event IDs above across the same 30-day lookback every other event-log read in this
    /// service uses. One flat, unfiltered-by-service list - callers group/window/walk it three
    /// different ways rather than this running three separate queries.
    /// </summary>
    public List<ServiceScmEvent> ReadServiceFailureEvents()
    {
        var result = new List<ServiceScmEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            string idFilter = string.Join(" or ", ScmFailureEventIds.Select(id => $"EventID={id}"));
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Service Control Manager'] and ({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 3000; // generous, same reasoning as ReadServiceStartDurations
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is null) continue;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - same known gap ReadLog already handles

                    string serviceName = ExtractScmServiceName(record.Id, message);
                    if (serviceName.Length == 0) continue; // can't confidently attribute this event to a service - drop it rather than guess

                    result.Add(new ServiceScmEvent
                    {
                        TimeCreated = record.TimeCreated.Value,
                        EventId = record.Id,
                        ServiceDisplayName = serviceName,
                        Message = Truncate(message, 300),
                        RestartCount = ExtractRestartCount(record.Id, message),
                    });
                }
            }
        }
        catch
        {
            // Log unavailable/access denied - degrade to "no failure history", same as every other
            // event-log read in this service.
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    private static string ExtractScmServiceName(int eventId, string message)
    {
        if (!ScmServiceNamePatterns.TryGetValue(eventId, out var regex)) return string.Empty;
        var m = regex.Match(message);
        return m.Success ? m.Groups["svc"].Value.Trim() : string.Empty;
    }

    private static int? ExtractRestartCount(int eventId, string message)
    {
        if (eventId is not (7031 or 7034)) return null;
        var m = RestartCountRegex.Match(message);
        return m.Success && int.TryParse(m.Groups["n"].Value, out int n) ? n : null;
    }
}
