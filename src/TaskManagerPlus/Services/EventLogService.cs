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

    // #223: USB/PnP re-enumeration churn - repeated device arrive/remove cycling from one device is
    // the classic "the whole PC hitches every few seconds" symptom (each re-enumeration briefly
    // spikes DPC/interrupt activity while the bus re-negotiates). Kernel-PnP 410/411/430 in the
    // System log cover the kernel-level device-node start/stop/removal transitions; the
    // DriverFrameworks-UserMode provider's own Operational channel covers UMDF driver instances
    // (many USB peripherals) that the kernel-level events alone don't capture.
    private static readonly Regex DeviceInstanceIdRegex = new(
        @"(?:USB|PCI|HID|ACPI|SWD|ROOT|STORAGE)\\[A-Za-z0-9_&.\\-]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly int[] KernelPnpChurnEventIds = { 410, 411, 430 };

    public List<UsbChurnRow> ReadUsbChurnEvents(TimeSpan window)
    {
        var byDevice = new Dictionary<string, (int Count, DateTime Last, string Description)>(StringComparer.OrdinalIgnoreCase);
        long maxAgeMs = (long)window.TotalMilliseconds;

        void ScanLog(string logName, string providerName, int[]? eventIds)
        {
            try
            {
                string idFilter = eventIds is null || eventIds.Length == 0
                    ? string.Empty
                    : $" and ({string.Join(" or ", eventIds.Select(id => $"EventID={id}"))})";
                var query = new EventLogQuery(logName, PathType.LogName,
                    $"*[System[Provider[@Name='{providerName}']{idFilter} and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]");

                using var reader = new EventLogReader(query);
                int count = 0;
                const int maxEvents = 2000; // generous - PnP churn is exactly the scenario that can legitimately produce thousands of entries
                while (count < maxEvents && reader.ReadEvent() is { } record)
                {
                    using (record)
                    {
                        count++;
                        string message;
                        try { message = record.FormatDescription() ?? string.Empty; }
                        catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                        var match = DeviceInstanceIdRegex.Match(message);
                        // A message that doesn't parse still counts toward total churn volume, just
                        // bucketed as unresolved rather than fabricating a device identity for it.
                        string key = match.Success ? match.Value : $"(unresolved — {providerName} event {record.Id})";
                        string desc = match.Success ? Truncate(message, 120) : string.Empty;
                        var time = record.TimeCreated ?? DateTime.MinValue;

                        if (byDevice.TryGetValue(key, out var existing))
                        {
                            byDevice[key] = (
                                existing.Count + 1,
                                time > existing.Last ? time : existing.Last,
                                existing.Description.Length > 0 ? existing.Description : desc);
                        }
                        else
                        {
                            byDevice[key] = (1, time, desc);
                        }
                    }
                }
            }
            catch
            {
                // Log/provider unavailable/access denied - contributes nothing from this source.
            }
        }

        ScanLog("System", "Microsoft-Windows-Kernel-PnP", KernelPnpChurnEventIds);
        ScanLog("Microsoft-Windows-DriverFrameworks-UserMode/Operational", "Microsoft-Windows-DriverFrameworks-UserMode", null);

        return byDevice
            .Select(kv => new UsbChurnRow
            {
                DeviceInstanceId = kv.Key,
                DeviceDescription = kv.Value.Description,
                EventCount = kv.Value.Count,
                LastEvent = kv.Value.Last,
            })
            .Where(r => r.EventCount > 1) // a single arrive/remove pair is normal (plug/unplug); only repeated churn is the symptom
            .OrderByDescending(r => r.EventCount)
            .ToList();
    }

    // #240: "Application Hang" (event ID 1002) - the Application-log counterpart to the crash scan
    // above (#1/#8's "Application Error"/event 1000), same generic provider-name convention Windows
    // uses for both. The event's own formatted message layout ("The program X version Y stopped
    // interacting with Windows...") is a stable, user-facing sentence rather than a documented,
    // versioned insertion-string contract, so this regexes the formatted text the same way
    // ExtractFaultingModule already does for crash entries, rather than indexing record.Properties
    // by position (which does vary by Windows version for this event).
    private static readonly Regex AppHangNameRegex = new(
        @"The program\s+(.+?)\s+version\s+(.+?)\s+stopped interacting", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HangTypeRegex = new(@"Hang [Tt]ype:\s*([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const string ApplicationHangProvider = "Application Hang";
    private const int ApplicationHangEventId = 1002;

    /// <summary>#240: ranks "top hanging apps in the last 30 days" with a best-effort hang type -
    /// complements #239's richer per-report Report.wer detail, which Windows prunes from
    /// ReportQueue/ReportArchive sooner than the event log itself keeps this event around.</summary>
    public List<AppHangEventSummary> ReadApplicationHangEvents(TimeSpan lookback)
    {
        var byApp = new Dictionary<string, (int Count, DateTime Last, string HangType)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            long maxAgeMs = (long)lookback.TotalMilliseconds;
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='{ApplicationHangProvider}'] and (EventID={ApplicationHangEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
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
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    var nameMatch = AppHangNameRegex.Match(message);
                    string appName = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : "(unknown)";
                    string hangType = HangTypeRegex.Match(message) is { Success: true } htm ? htm.Groups[1].Value.Trim() : string.Empty;
                    var time = record.TimeCreated ?? DateTime.MinValue;

                    if (byApp.TryGetValue(appName, out var existing))
                    {
                        byApp[appName] = (
                            existing.Count + 1,
                            time > existing.Last ? time : existing.Last,
                            existing.HangType.Length > 0 ? existing.HangType : hangType);
                    }
                    else
                    {
                        byApp[appName] = (1, time, hangType);
                    }
                }
            }
        }
        catch
        {
            // Log/provider unavailable/access denied - degrade to "no hang events found".
        }

        return byApp
            .Select(kv => new AppHangEventSummary
            {
                AppName = kv.Key,
                Count = kv.Value.Count,
                LastSeen = kv.Value.Last,
                HangType = string.IsNullOrEmpty(kv.Value.HangType) ? "Unknown" : kv.Value.HangType,
            })
            .OrderByDescending(r => r.Count)
            .ToList();
    }
}
