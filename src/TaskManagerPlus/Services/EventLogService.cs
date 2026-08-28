using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
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
    // Public so StabilityViewModel's log-coverage text (round 13, item 12) can describe the same
    // window number this service actually queries with, rather than a second hardcoded literal.
    public const int LookbackDays = 30;
    private const int MaxEventsPerLog = 60;

    // Kernel-Power 41 = "The system has rebooted without cleanly shutting down first" - the
    // classic signal WhoCrashed/Reliability Monitor use for "this was a real crash". EventLog
    // 6008 is the older, plain-text version of the same event.
    private const int KernelPowerEventId = 41;
    private const int LegacyUncleanShutdownEventId = 6008;
    private const int TdrEventId = 4101;

    // Windows Error Reporting's own "a Blue Screen just happened" summary entry (Application log).
    private const int BlueScreenEventId = 1001;

    // #191: a *different* provider/channel from BlueScreenEventId above despite sharing the same
    // event ID number (1001) - this one is System-log, and its message text carries the full
    // bugcheck line ("0x00000133 (0x..., 0x..., 0x..., 0x...)") plus the actual dump file path,
    // which Kernel-Power 41's insertion strings never do. See ReadWerBugcheckEvents.
    private const string WerSystemErrorReportingProvider = "Microsoft-Windows-WER-SystemErrorReporting";
    private const int WerBugcheckEventId = 1001;

    private static readonly Regex FaultingModuleRegex = new(@"Faulting module name:\s*([^,\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // #191: the bugcheck code plus its four parameters, as WER-SystemErrorReporting 1001 renders
    // them in its message text - not a documented, versioned contract either, but a far more
    // reliable one than Kernel-Power 41's raw property-index guess (this is the same text format
    // WhoCrashed/BlueScreenView parse from the equivalent minidump header).
    private static readonly Regex WerBugcheckLineRegex = new(
        @"(0x[0-9A-Fa-f]{8})\s*\(\s*(0x[0-9A-Fa-f]+)\s*,\s*(0x[0-9A-Fa-f]+)\s*,\s*(0x[0-9A-Fa-f]+)\s*,\s*(0x[0-9A-Fa-f]+)\s*\)",
        RegexOptions.Compiled);
    private static readonly Regex WerDumpPathRegex = new(@"([A-Za-z]:\\[^\r\n]*?\.dmp)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        // Round 13: items 1/2/8 (authoritative bugcheck record + WER join + WER-summary fallback),
        // 3/4 (classified unexpected-shutdown history), 5/6 (shutdown/restart/boot timeline), 7
        // (dump-creation-failed detection), 9/10 (WHEA hardware errors + partial record decode), 11
        // (Microsoft's own Reliability Monitor index), 12 (log-coverage health check). Each is its
        // own independently-wrapped query/read, same "one provider/source, degrade to empty on any
        // failure" shape as every existing method in this file.
        //
        // Round 15: items 33/36 need the WHEA-error list and the sleep/resume event times *before*
        // the bugcheck records are built, so those two reads happen first and EnrichBugCheckRecord
        // joins them onto each record (0x9F -> sleep/resume, 0x124 -> nearest WHEA record) - item
        // 28-32/35/37's plain per-code decode is attached the same way, via BugcheckDecoder.
        var wheaErrors = ReadWheaErrors();
        var sleepResumeEvents = ReadSleepResumeEventTimes();
        var bugChecks = ReadBugCheckRecords()
            .Select(b => EnrichBugCheckRecord(b, sleepResumeEvents, wheaErrors))
            .ToList();
        // Round 17 chunk 64-70, item 68: read before ShutdownTimeline below (not after, like every
        // prior round) so ReadShutdownTimeline can cross-reference each dirty boot against these
        // same classified Kernel-Power 41 records instead of re-querying the provider itself.
        var unexpectedShutdowns = ReadUnexpectedShutdowns();

        // #191: prefer the richer WER-SystemErrorReporting 1001 bugcheck detail (all four
        // parameters + dump path) over the event-41 property-index guess ReadLog already filled in
        // above, when a WER 1001 was logged within 10 minutes of the Kernel-Power 41 - the existing
        // guess stays untouched (fallback) for any 41 with no matching WER 1001 nearby. Independent
        // of the authoritative BugCheckRecord path above (round 13, items 1/2/8), which matches off
        // the BugCheck-provider's own 1001 event rather than the WER-summary one - both are kept,
        // this one enriching the flat RecentEvents list directly for immediate display.
        var werBugchecks = ReadWerBugcheckEvents();
        foreach (var kp41 in events.Where(e => e.EventId == KernelPowerEventId))
        {
            var nearest = werBugchecks
                .Where(w => Math.Abs((w.Timestamp - kp41.TimeCreated).TotalMinutes) < 10)
                .OrderBy(w => Math.Abs((w.Timestamp - kp41.TimeCreated).TotalMinutes))
                .FirstOrDefault();
            if (nearest is null) continue;
            kp41.BugcheckDetail = nearest.Detail;
            kp41.BugcheckCode = nearest.Detail.Code; // authoritative - overrides the property-index guess
        }

        return new StabilitySnapshot
        {
            RecentEvents = events.Take(MaxEventsPerLog).ToList(),
            WasLastShutdownUnexpected = wasUnexpected,
            LastUnexpectedShutdown = mostRecentShutdownEvent?.TimeCreated,
            TdrEventCount = tdrEvents.Count,
            LastTdrEvent = tdrEvents.FirstOrDefault()?.TimeCreated,
            LastCrashTime = lastCrash?.TimeCreated,
            Minidumps = ReadMinidumps(shutdownEvents, bugChecks, werBugchecks),
            DailyCounts = BuildDailyCounts(events),
            LowMemoryEventCount = lowMemCount,
            LastLowMemoryEvent = lowMemLast,
            LatestBugCheck = bugChecks.FirstOrDefault(),
            LastShutdownCause = unexpectedShutdowns.OrderByDescending(u => u.TimeCreated).FirstOrDefault()?.Cause,
            UnexpectedShutdowns = unexpectedShutdowns,
            ShutdownTimeline = ReadShutdownTimeline(unexpectedShutdowns),
            DumpFailures = ReadDumpFailures(),
            WheaErrors = wheaErrors,
            ReliabilityMetrics = ReadReliabilityMetrics(),
            LogHealth = ReadLogHealth(),
            TdrEventDetails = ReadTdrEventDetails(),
            TdrSettings = ReadTdrRegistrySettings(),
        };
    }

    // ---------------------------------------------------------------------------------------
    // Round 15, items 28-37: bugcheck code/parameter decoding, joined onto each BugCheckRecord.
    // ---------------------------------------------------------------------------------------

    // Item 33: how close a Kernel-Power sleep/resume event has to be to a 0x9F crash's own
    // timestamp to count as "happened during sleep/resume" - generous enough to allow for the
    // event-log timestamp jitter already tolerated elsewhere in this file (e.g. the ±10-minute
    // Minidump/Kernel-Power-41 correlation window), tight enough that an unrelated sleep/resume
    // cycle earlier or later in the day doesn't get treated as related.
    private const double SleepResumeJoinWindowMinutes = 5;

    // Item 36: how close a WHEA-Logger record's own timestamp has to be to a 0x124 crash to be
    // treated as "the" hardware-error record that likely caused it.
    private const double WheaJoinWindowMinutes = 10;

    /// <summary>Items 28-37 (parameter decode) + 33/69 (sleep/resume) + 36 (WHEA join): attaches a
    /// BugcheckDecodedInfo plus the two timestamp-based joins to one already-read BugCheckRecord.
    /// BugCheckRecord's properties are all init-only, so this returns a new instance rather than
    /// mutating - cheap, since there are at most a few dozen records in the lookback window.</summary>
    private static BugCheckRecord EnrichBugCheckRecord(BugCheckRecord record, List<DateTime> sleepResumeEvents, List<WheaErrorEvent> wheaErrors)
    {
        // Item 69: the sleep/resume join applies to every stop code now (item 33 originally gated
        // this to 0x9F/DRIVER_POWER_STATE_FAILURE only - see HappenedDuringSleepResume's own
        // remarks on BugCheckRecord/MinidumpInfo for why item 69 widened it), so this is computed
        // before the "can't even parse the code" early-return below rather than after it.
        bool nearSleepResume = sleepResumeEvents.Any(t => Math.Abs((t - record.TimeCreated).TotalMinutes) <= SleepResumeJoinWindowMinutes);

        if (!BugcheckHex.TryParseCode(record.StopCode, out var code))
        {
            return record with
            {
                Decoded = BugcheckDecoder.Decode(record.StopCode, record.Parameters),
                HappenedDuringSleepResume = nearSleepResume,
            };
        }

        string? wheaJoin = null;
        if (code == 0x00000124)
        {
            var nearest = wheaErrors
                .Where(w => Math.Abs((w.TimeCreated - record.TimeCreated).TotalMinutes) <= WheaJoinWindowMinutes)
                .OrderBy(w => Math.Abs((w.TimeCreated - record.TimeCreated).TotalMinutes))
                .FirstOrDefault();
            if (nearest is not null)
                wheaJoin = $"{nearest.Decoded} (WHEA-Logger event {nearest.EventId} at {nearest.TimeCreated:g})";
        }

        return record with
        {
            Decoded = BugcheckDecoder.Decode(record.StopCode, record.Parameters),
            HappenedDuringSleepResume = nearSleepResume,
            WheaJoinText = wheaJoin,
        };
    }

    // Item 33: Kernel-Power sleep/resume markers - 42 "entering sleep", 107/187 resume/wake
    // markers - the same provider ReadUnexpectedShutdowns already reads (41) but a different set
    // of event IDs, so this is its own targeted query rather than reusing that one's result.
    private static List<DateTime> ReadSleepResumeEventTimes()
    {
        var result = new List<DateTime>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=42 or EventID=107 or EventID=187) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 500;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is { } t) result.Add(t);
                }
            }
        }
        catch
        {
            // Provider/log unavailable - no sleep/resume cross-reference available for item 33.
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------
    // Round 15, item 34: TDR detail beyond a bare count - per-event driver/app, plus the live
    // TdrDelay/TdrDdiDelay/TdrLevel registry settings.
    // ---------------------------------------------------------------------------------------

    private static readonly Regex TdrDriverRegex = new(@"[Dd]isplay driver\s+(\S+)\s+stopped responding", RegexOptions.Compiled);

    /// <summary>Item 34: event 4101's own insertion strings name the display driver (property 0)
    /// and, on Windows versions/drivers that populate it, the application whose GPU context was
    /// reset (property 1) - read positionally like every other legacy-provider parse in this file,
    /// with a regex fallback against the formatted message text (which always names the driver)
    /// when the named property isn't present. This is a separate targeted query rather than
    /// reusing the Level-filtered `events` list the plain TdrEventCount tile is built from, the
    /// same "Warning-level event needs its own query" shape ReadLowMemoryEvents already uses.</summary>
    private static List<TdrEventDetail> ReadTdrEventDetails()
    {
        var result = new List<TdrEventDetail>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Display'] and (EventID={TdrEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 100;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    string? driver = null, app = null;
                    try
                    {
                        if (record.Properties.Count > 0) driver = record.Properties[0].Value as string;
                        if (record.Properties.Count > 1) app = record.Properties[1].Value as string;
                    }
                    catch { /* fall through to the regex parse of the formatted message below */ }

                    if (string.IsNullOrWhiteSpace(driver))
                    {
                        var m = TdrDriverRegex.Match(message);
                        if (m.Success) driver = m.Groups[1].Value;
                    }

                    result.Add(new TdrEventDetail
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        Driver = string.IsNullOrWhiteSpace(driver) ? null : driver,
                        Application = string.IsNullOrWhiteSpace(app) ? null : app,
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "no TDR event detail available" (the plain
            // count/last-seen tile above still works independently via the Level-filtered scan).
        }
        return result;
    }

    /// <summary>Item 34: the three registry values that actually control TDR's timeout behavior -
    /// null fields mean "not set, Windows' own undocumented built-in default applies" rather than
    /// a fabricated number (CLAUDE.md's "degrade to Unknown, never fabricate").</summary>
    private static TdrRegistrySettings ReadTdrRegistrySettings()
    {
        const string keyPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        int? delay = null, ddiDelay = null, level = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is not null)
            {
                if (key.GetValue("TdrDelay") is { } d) delay = Convert.ToInt32(d);
                if (key.GetValue("TdrDdiDelay") is { } dd) ddiDelay = Convert.ToInt32(dd);
                if (key.GetValue("TdrLevel") is { } l) level = Convert.ToInt32(l);
            }
        }
        catch
        {
            // Key/values not present, or access denied - Windows falls back to its own
            // undocumented built-in defaults, not something this app should guess at.
        }

        string levelText = level switch
        {
            0 => "0 — detection disabled",
            1 => "1 — detection enabled, recovery disabled (bugchecks instead of recovering)",
            2 => "2 — detection and recovery both disabled",
            3 => "3 — detection and recovery enabled (Windows default)",
            null => "not set (Windows default applies)",
            _ => $"{level} (unrecognized value)",
        };

        return new TdrRegistrySettings
        {
            TdrDelaySeconds = delay,
            TdrDdiDelaySeconds = ddiDelay,
            TdrLevel = level,
            TdrLevelText = levelText,
        };
    }

    private sealed class WerBugcheckHit
    {
        public DateTime Timestamp { get; init; }
        public BugcheckDetail Detail { get; init; } = new();
    }

    /// <summary>#191: reads Microsoft-Windows-WER-SystemErrorReporting 1001 (System log - a
    /// different provider/channel from WerReportService's Application-log WER 1001, despite the
    /// same event ID number) and regex-parses each one's message text for the bugcheck line and
    /// dump file path. An event whose text doesn't match the expected "0x... (0x...,0x...,0x...,
    /// 0x...)" shape is skipped (older Windows editions, or a non-bugcheck WER-SystemErrorReporting
    /// entry) rather than producing a half-filled BugcheckDetail - degrades to "no richer detail",
    /// leaving the event-41 guess as the only source, same as every other event-log read in this
    /// service.</summary>
    private static List<WerBugcheckHit> ReadWerBugcheckEvents()
    {
        var hits = new List<WerBugcheckHit>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{WerSystemErrorReportingProvider}'] and (EventID={WerBugcheckEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 200; // generous - a well-behaved PC has few to none of these in 30 days
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is null) continue;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - skip this one

                    var lineMatch = WerBugcheckLineRegex.Match(message);
                    if (!lineMatch.Success) continue; // text doesn't carry the expected bugcheck line - nothing usable

                    var dumpMatch = WerDumpPathRegex.Match(message);
                    hits.Add(new WerBugcheckHit
                    {
                        Timestamp = record.TimeCreated.Value,
                        Detail = new BugcheckDetail
                        {
                            Code = lineMatch.Groups[1].Value,
                            Parameter1 = lineMatch.Groups[2].Value,
                            Parameter2 = lineMatch.Groups[3].Value,
                            Parameter3 = lineMatch.Groups[4].Value,
                            Parameter4 = lineMatch.Groups[5].Value,
                            DumpFilePath = dumpMatch.Success ? dumpMatch.Groups[1].Value.Trim() : null,
                        },
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable, or this Windows edition doesn't log this event at all -
            // degrade to "no richer detail" (the event-41 guess remains the only source).
        }
        return hits;
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
                        // #168: a fuller, non-truncated parse for .NET Runtime 1026 / Application
                        // Error 1000 specifically - every other event ID keeps the same truncated
                        // Message as before (see StabilityEvent.DisplayDetail).
                        ExceptionDetail = record.Id is 1026 or 1000 ? WerReportService.ParseManagedExceptionDetail(record.Id, message) : null,
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

    /// <summary>
    /// Round 13, item 1: for each minidump file, prefers the authoritative BugCheck 1001 record
    /// (see ReadBugCheckRecords) whose own dump path matches this file by name - it carries the
    /// real stop code, all four bugcheck parameters, and (item 2) a joined WER report, not just a
    /// best-effort nearby-timestamp guess. Only falls back to the old ±10-minute Kernel-Power-41
    /// timestamp correlation when no matching authoritative record was found (an older Windows
    /// version without the BugCheck provider, or a log that's already rolled the event off) -
    /// parsing the bugcheck code directly out of the .dmp binary format would need a full
    /// MINIDUMP-stream reader, a much larger and more fragile undertaking than either of these.
    /// #191: independently, an exact dump-file-path match from a WER-SystemErrorReporting 1001
    /// event (<paramref name="werBugchecks"/>) - when Windows itself named this dump file in that
    /// summary event - additionally supplies the richer BugcheckDetail (all four parameters + dump
    /// path in one place), layered on top of whichever match above found the base record, since the
    /// two sources come from different providers and aren't mutually exclusive.
    /// </summary>
    private static List<MinidumpInfo> ReadMinidumps(List<StabilityEvent> shutdownEvents, List<BugCheckRecord> bugChecks, List<WerBugcheckHit> werBugchecks)
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

                var matched = bugChecks.FirstOrDefault(b =>
                    b.DumpPath is not null && string.Equals(Path.GetFileName(b.DumpPath), info.Name, StringComparison.OrdinalIgnoreCase));

                // #191: an exact dump-file-path match from a WER 1001 event, when one exists, adds
                // the richer BugcheckDetail on top of whichever match below found the base record.
                var werMatch = werBugchecks.FirstOrDefault(w =>
                    w.Detail.DumpFilePath is { } p && string.Equals(Path.GetFileName(p), info.Name, StringComparison.OrdinalIgnoreCase));

                if (matched is not null)
                {
                    dumps.Add(new MinidumpInfo
                    {
                        FileName = info.Name,
                        Timestamp = info.LastWriteTime,
                        BugcheckCode = matched.StopCode,
                        BugcheckParameters = matched.Parameters,
                        WerReport = matched.WerReport,
                        IsAuthoritative = !matched.FromWerSummary,
                        // Round 15, items 28-37: reuse the same decode + joins already computed
                        // for the BugCheckRecord itself (EnrichBugCheckRecord), rather than
                        // recomputing them from scratch for this per-file view of the same record.
                        Decoded = matched.Decoded,
                        HappenedDuringSleepResume = matched.HappenedDuringSleepResume,
                        WheaJoinText = matched.WheaJoinText,
                        BugcheckDetail = werMatch?.Detail,
                    });
                    continue;
                }

                var nearest = bugchecks
                    .Where(e => Math.Abs((e.TimeCreated - info.LastWriteTime).TotalMinutes) < 10)
                    .OrderBy(e => Math.Abs((e.TimeCreated - info.LastWriteTime).TotalMinutes))
                    .FirstOrDefault();

                dumps.Add(new MinidumpInfo
                {
                    FileName = info.Name,
                    Timestamp = info.LastWriteTime,
                    BugcheckCode = werMatch?.Detail.Code ?? nearest?.BugcheckCode,
                    // The old nearby-timestamp fallback never recovered parameters (see
                    // MinidumpInfo.BugcheckParameters' own remarks), so there's nothing for
                    // BugcheckDecoder to decode beyond the bare code itself.
                    Decoded = nearest?.BugcheckCode is not null ? BugcheckDecoder.Decode(nearest.BugcheckCode, Array.Empty<string>()) : null,
                    BugcheckDetail = werMatch?.Detail,
                });
            }
        }
        catch
        {
            // Folder missing or access denied - an empty list just means nothing to show.
        }
        return dumps.OrderByDescending(d => d.Timestamp).ToList();
    }

    // ---------------------------------------------------------------------------------------
    // Round 13, items 1/2/8: authoritative BugCheck 1001 record, joined WER report, WER-summary
    // fallback.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Item 1: reads every occurrence of the `BugCheck` provider's System-log event 1001 ("The
    /// computer has rebooted from a bugcheck") in the lookback window - a legacy classic ETW
    /// provider whose insertion strings are read positionally (like ExtractBugcheckCode already
    /// does for Kernel-Power 41): [0] stop code, [1]-[4] the four bugcheck parameters, [5] dump
    /// path, [6] WER Report Id. When the provider itself isn't present in the log at all (older
    /// Windows versions, or a log that's rolled the event off), item 8's
    /// WER-SystemErrorReporting 1001 summary is used as a second, independent source instead.
    /// </summary>
    private static List<BugCheckRecord> ReadBugCheckRecords()
    {
        var result = new List<BugCheckRecord>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='BugCheck'] and (EventID=1001) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 60;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    try
                    {
                        var props = record.Properties;
                        string? stop = props.Count > 0 ? FormatBugcheckValue(props[0].Value) : null;
                        if (stop is null) continue;

                        var parameters = new List<string>();
                        for (int i = 1; i <= 4 && i < props.Count; i++)
                        {
                            var v = FormatBugcheckValue(props[i].Value);
                            if (v is not null) parameters.Add(v);
                        }

                        string? dumpPath = props.Count > 5 ? props[5].Value as string : null;
                        string? reportId = props.Count > 6 ? props[6].Value as string : null;
                        dumpPath = string.IsNullOrWhiteSpace(dumpPath) ? null : dumpPath;
                        reportId = string.IsNullOrWhiteSpace(reportId) ? null : reportId;

                        result.Add(new BugCheckRecord
                        {
                            TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                            StopCode = stop,
                            Parameters = parameters.ToArray(),
                            DumpPath = dumpPath,
                            ReportId = reportId,
                            FromWerSummary = false,
                            WerReport = reportId is null ? null : ResolveWerReport(reportId),
                        });
                    }
                    catch { /* one malformed record shouldn't stop the rest of the scan */ }
                }
            }
        }
        catch
        {
            // Provider not present in this log (older Windows versions can lack it), or access
            // denied - fall through to the WER-summary source below.
        }

        if (result.Count == 0)
            result.AddRange(ReadWerSummaryBugChecks());

        return result.OrderByDescending(r => r.TimeCreated).ToList();
    }

    /// <summary>BugCheck 1001's insertion strings come through as either an already-formatted
    /// "0x........" string or a raw numeric value depending on how the provider logged it - this
    /// normalizes either shape to a consistent "0x" + 8-hex-digit string, the same display format
    /// ExtractBugcheckCode already uses for Kernel-Power 41.</summary>
    private static string? FormatBugcheckValue(object? raw)
    {
        if (raw is null) return null;
        if (raw is string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return s;
            return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hexVal)
                ? $"0x{hexVal:X8}"
                : s;
        }
        try
        {
            ulong v = Convert.ToUInt64(raw);
            return $"0x{v:X8}";
        }
        catch
        {
            return raw.ToString();
        }
    }

    /// <summary>
    /// Item 8: Microsoft-Windows-WER-SystemErrorReporting's own "BlueScreen" 1001 summary entry -
    /// a second, independent confirmation of a bugcheck, used only to fill in when the BugCheck
    /// provider's own 1001 entry is missing. Parsed from the event's formatted message text (regex,
    /// like ExtractFaultingModule already does for Application-log crash entries) rather than
    /// positional properties, since this provider's own property layout isn't the one item 1's
    /// parsing was derived from. Never recovers a Report Id, so WerReport is always null for these.
    /// </summary>
    private static List<BugCheckRecord> ReadWerSummaryBugChecks()
    {
        var result = new List<BugCheckRecord>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and (EventID=1001) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 60;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    var hexGroupMatch = Regex.Match(message,
                        @"0x[0-9A-Fa-f]{8}(?:\s*\(\s*0x[0-9A-Fa-f]{8}(?:\s*,\s*0x[0-9A-Fa-f]{8}){0,3}\s*\))?");
                    if (!hexGroupMatch.Success) continue;

                    var allHex = Regex.Matches(hexGroupMatch.Value, @"0x[0-9A-Fa-f]{8}").Select(m => m.Value).ToList();
                    var dumpMatch = Regex.Match(message, @"([A-Za-z]:\\[^\r\n]*?\.dmp)", RegexOptions.IgnoreCase);

                    result.Add(new BugCheckRecord
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        StopCode = allHex[0],
                        Parameters = allHex.Skip(1).ToArray(),
                        DumpPath = dumpMatch.Success ? dumpMatch.Groups[1].Value : null,
                        ReportId = null,
                        FromWerSummary = true,
                        WerReport = null,
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "no WER-summary confirmation either".
        }
        return result;
    }

    /// <summary>
    /// Item 2: resolves a BugCheck 1001 Report Id GUID to its WER ReportArchive folder under
    /// %ProgramData%\Microsoft\Windows\WER\ReportArchive. There's no simple index from Report Id to
    /// folder name, so this scans each report folder's own Report.wer text file (a key=value
    /// format WER itself writes) for the GUID - a best-effort text search, not a WER API
    /// integration. OS version / secure-boot state are pulled from whatever keys Report.wer
    /// happens to carry (they aren't guaranteed present on every report); AttachedFiles is simply
    /// a directory listing of the matched folder, which is normally the most reliable way to see
    /// what WER actually archived (typically including the .dmp itself alongside Report.wer).
    /// </summary>
    private static WerReportFolderMetadata? ResolveWerReport(string reportId)
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var archiveRoot = Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive");
            if (!Directory.Exists(archiveRoot)) return null;

            foreach (var dir in Directory.GetDirectories(archiveRoot))
            {
                var werFile = Path.Combine(dir, "Report.wer");
                if (!File.Exists(werFile)) continue;

                string text;
                try { text = File.ReadAllText(werFile); }
                catch { continue; }

                if (text.IndexOf(reportId, StringComparison.OrdinalIgnoreCase) < 0) continue;

                string? osVersion = ExtractWerValue(text, "OSVersionInformation") ?? ExtractWerValue(text, "OsVersion");
                string? secureBoot = ExtractWerValue(text, "SecureBootState") ?? ExtractWerValue(text, "SecureBoot");

                var files = new List<string>();
                try
                {
                    files = Directory.GetFiles(dir)
                        .Select(Path.GetFileName)
                        .Where(f => !string.IsNullOrEmpty(f))
                        .Select(f => f!)
                        .ToList();
                }
                catch { /* leave whatever was gathered, or empty */ }

                return new WerReportFolderMetadata
                {
                    ReportFolder = dir,
                    OsVersion = osVersion,
                    SecureBootState = secureBoot,
                    AttachedFiles = files,
                };
            }
        }
        catch
        {
            // ReportArchive missing/access denied - "no full crash record found", not an error.
        }
        return null;
    }

    private static string? ExtractWerValue(string text, string key)
    {
        var match = Regex.Match(text, $@"^{Regex.Escape(key)}=(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // ---------------------------------------------------------------------------------------
    // Round 13, items 3/4: classified, full-history unexpected shutdowns.
    // ---------------------------------------------------------------------------------------

    /// <summary>Item 4: every Kernel-Power 41 occurrence in the lookback window (not just the most
    /// recent), each classified per item 3 - see ClassifyPowerEvent.</summary>
    private static List<UnexpectedShutdownRecord> ReadUnexpectedShutdowns()
    {
        var result = new List<UnexpectedShutdownRecord>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID={KernelPowerEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 100;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    var (cause, bugcheck, uptime) = ClassifyPowerEvent(record);
                    result.Add(new UnexpectedShutdownRecord
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        Cause = cause,
                        BugcheckCode = bugcheck,
                        UptimeBeforeCrash = uptime,
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "no unexpected-shutdown history available".
        }
        return result;
    }

    /// <summary>
    /// Item 3: best-effort decode of Kernel-Power event 41's own named properties - the commonly
    /// observed (not a formally versioned, documented-per-release contract) property layout is: 0
    /// BugcheckCode, 1-4 BugcheckParameter1-4, 5 SleepInProgress, 6 PowerButtonTimestamp (a FILETIME,
    /// 0 when the shutdown wasn't power-button-initiated), 8 Checkpoint. Each field is read
    /// defensively (bounds-checked, wrapped) so a read that fails or falls outside the expected
    /// range degrades just that one field rather than aborting the whole classification - the same
    /// per-field tolerance ReadServiceStartDurations already uses for its own event parsing.
    ///
    /// The cause itself is a heuristic ("quick flag, not a verdict" per CLAUDE.md), not a
    /// documented Windows classification: a nonzero bugcheck code means a real bugcheck; else a
    /// nonzero PowerButtonTimestamp means the power button was held; else a Checkpoint of 0 (no
    /// shutdown progress was ever recorded) is reported as looking like a sudden loss of power;
    /// anything else is reported as a hard hang (a shutdown was in progress but never completed).
    /// "Uptime before crash" scans the remaining numeric properties for a plausible minutes-since-
    /// boot value instead of trusting one fixed index, since PowerOnMinutes' own position isn't
    /// consistent across Windows versions either.
    /// </summary>
    private static (ShutdownCause Cause, string? Bugcheck, TimeSpan? Uptime) ClassifyPowerEvent(EventRecord record)
    {
        ulong bugcheckCode = 0, powerButtonTimestamp = 0, checkpoint = 0, sleepInProgress = 0;
        TimeSpan? uptime = null;
        try
        {
            var props = record.Properties;
            if (props.Count > 0) TryToUInt64(props[0].Value, out bugcheckCode);
            if (props.Count > 5) TryToUInt64(props[5].Value, out sleepInProgress);
            if (props.Count > 6) TryToUInt64(props[6].Value, out powerButtonTimestamp);
            if (props.Count > 8) TryToUInt64(props[8].Value, out checkpoint);

            for (int i = 9; i < props.Count; i++)
            {
                if (TryToUInt64(props[i].Value, out var v) && v > 0 && v < 5_256_000) // < ~10 years
                {
                    uptime = TimeSpan.FromMinutes(v);
                    break;
                }
            }
        }
        catch
        {
            // Leave defaults - falls through to the HardHang/Unknown-ish branch below rather than
            // throwing out of the whole classification.
        }

        string? bugcheckStr = bugcheckCode == 0 ? null : $"0x{bugcheckCode:X8}";

        ShutdownCause cause;
        if (bugcheckCode != 0) cause = ShutdownCause.Bugcheck;
        else if (powerButtonTimestamp != 0) cause = ShutdownCause.PowerButtonHeld;
        else if (checkpoint == 0 && sleepInProgress == 0) cause = ShutdownCause.PowerLoss;
        else cause = ShutdownCause.HardHang;

        return (cause, bugcheckStr, uptime);
    }

    private static bool TryToUInt64(object? value, out ulong result)
    {
        result = 0;
        if (value is null) return false;
        try { result = Convert.ToUInt64(value); return true; }
        catch { return false; }
    }

    // ---------------------------------------------------------------------------------------
    // Round 13, items 5/6: shutdown & restart timeline, boot/shutdown pairing gap detection.
    // ---------------------------------------------------------------------------------------

    private static readonly Regex ShutdownProcessRegex = new(@"^The process\s+(\S+)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ShutdownUserRegex = new(@"on behalf of user\s+(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ShutdownReasonRegex = new(@"for the following reason:\s*(.+?)(?:\r?\n|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ShutdownTypeRegex = new(@"Shutdown Type:\s*(\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Items 5/6: builds the merged "shutdown &amp; restart timeline" from three independent
    /// sources - User32 1074 (who/what/why initiated each shutdown or restart, item 5), the
    /// EventLog service's own start/stop markers (6005/6006/6009/6013, item 5), and a chronological
    /// pairing walk over Kernel-General 12/13 + Kernel-Boot 20/27 boot/shutdown markers that flags
    /// any boot not preceded by a matching clean-shutdown marker as dirty (item 6) - catching a
    /// dirty boot even when Kernel-Power 41 itself was never logged (e.g. the power was held past
    /// the point the OS could log anything at all).
    ///
    /// Round 17 chunk 64-70, item 68: each dirty boot found below is additionally checked against
    /// DetectFreezeWithoutCrash, using the already-classified unexpectedShutdowns list Query()
    /// read just before calling this method - no second Kernel-Power 41 query.
    /// </summary>
    private static List<ShutdownTimelineEntry> ReadShutdownTimeline(List<UnexpectedShutdownRecord> unexpectedShutdowns)
    {
        var entries = new List<ShutdownTimelineEntry>();
        long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;

        // Item 5: who/what initiated each shutdown/restart, and why - parsed from the event's own
        // formatted message text (regex, like ExtractFaultingModule already does elsewhere) rather
        // than positional properties, since this event's insertion-string count/order varies with
        // how many optional fields (comment, etc.) were populated.
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='User32'] and (EventID=1074) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 200;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    string? process = ShutdownProcessRegex.Match(message) is { Success: true } pm ? pm.Groups[1].Value : null;
                    string? user = ShutdownUserRegex.Match(message) is { Success: true } um ? um.Groups[1].Value : null;
                    string? reason = ShutdownReasonRegex.Match(message) is { Success: true } rm ? rm.Groups[1].Value.Trim() : null;
                    string? type = ShutdownTypeRegex.Match(message) is { Success: true } tm ? tm.Groups[1].Value : null;
                    string kind = string.Equals(type, "restart", StringComparison.OrdinalIgnoreCase)
                        ? "Restart requested" : "Shutdown requested";

                    entries.Add(new ShutdownTimelineEntry
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        Kind = kind,
                        Process = process,
                        User = user,
                        Reason = reason,
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - contributes nothing to the timeline.
        }

        // Item 5: log start/stop/uptime markers - not much decoding needed beyond "what happened".
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='EventLog'] and (EventID=6005 or EventID=6006 or EventID=6009 or EventID=6013) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 200;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string kind = record.Id switch
                    {
                        6005 => "Event log service started (boot)",
                        6006 => "Event log service stopped (clean shutdown)",
                        6009 => "Windows started",
                        6013 => "Uptime report",
                        _ => "Log event",
                    };
                    entries.Add(new ShutdownTimelineEntry { TimeCreated = record.TimeCreated ?? DateTime.MinValue, Kind = kind });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - contributes nothing to the timeline.
        }

        // Item 6: boot/shutdown pairing gap detection, walked chronologically (oldest first) so
        // each boot can be checked against the shutdown markers that precede it.
        var boots = new List<DateTime>();
        var shutdowns = new List<DateTime>();
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and (EventID=12 or EventID=13) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]");
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 300;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    var time = record.TimeCreated ?? DateTime.MinValue;
                    if (record.Id == 13) shutdowns.Add(time); else boots.Add(time);
                }
            }
        }
        catch { /* contributes nothing to pairing detection below */ }

        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and (EventID=20 or EventID=27) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]");
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 300;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    boots.Add(record.TimeCreated ?? DateTime.MinValue);
                }
            }
        }
        catch { /* contributes nothing to pairing detection below */ }

        boots.Sort();
        var remainingShutdowns = shutdowns.OrderBy(s => s).ToList();
        foreach (var boot in boots)
        {
            DateTime? precedingShutdown = remainingShutdowns.Where(s => s < boot).Cast<DateTime?>().OrderByDescending(s => s).FirstOrDefault();
            bool isDirty = precedingShutdown is null;

            // Item 68: only a dirty boot can possibly be a "freeze without crash" - a clean boot
            // already had a matching shutdown marker, so there's nothing to explain.
            string? freezeLabel = null;
            List<string> freezeContext = new();
            if (isDirty)
                (freezeLabel, freezeContext) = DetectFreezeWithoutCrash(boot, unexpectedShutdowns);

            entries.Add(new ShutdownTimelineEntry
            {
                TimeCreated = boot,
                Kind = "Boot",
                IsDirtyBoot = isDirty,
                FreezeWithoutCrashLabel = freezeLabel,
                EventsBeforeSilence = freezeContext,
            });
            if (precedingShutdown is { } used) remainingShutdowns.Remove(used);
        }

        return entries.OrderByDescending(e => e.TimeCreated).Take(150).ToList();
    }

    // Item 68: how close a Kernel-Power 41 event's own timestamp has to be to a dirty boot to be
    // treated as "the" record explaining it - a little wider than the ±5-minute window
    // WasLastShutdownUnexpected uses for the same K-P41-to-boot correlation, since Kernel-General
    // 12's boot marker and Kernel-Power 41 aren't always logged in the very same minute.
    private const double FreezeJoinWindowMinutes = 10;

    // Item 68: how close a minidump file's own last-write time has to be to the Kernel-Power 41
    // timestamp to count as "a dump WAS written for this crash" - if nothing matches within this
    // window, the boot is treated as dump-less.
    private const double FreezeDumpJoinWindowHours = 6;

    // Item 68: how many System-log events to show as "what was happening right before the
    // silence" context for a detected freeze.
    private const int FreezeContextEventCount = 6;

    /// <summary>
    /// Item 68: labels a dirty boot as "freeze without crash" (a true hard hang or sudden power
    /// loss, not a bugcheck Windows itself ever got to record) when three conditions all hold: the
    /// nearest Kernel-Power 41 event recorded no bugcheck code, no minidump file was written near
    /// that time, and - by virtue of this being a dirty boot in the first place - event-log
    /// activity simply stopped rather than recording any clean-shutdown marker. "Quick flag, not a
    /// verdict" per CLAUDE.md: the last few events before the silence are returned as context so
    /// the label can be sanity-checked rather than trusted blindly.
    /// </summary>
    private static (string? Label, List<string> Context) DetectFreezeWithoutCrash(DateTime bootTime, List<UnexpectedShutdownRecord> unexpectedShutdowns)
    {
        var nearest = unexpectedShutdowns
            .Where(u => Math.Abs((u.TimeCreated - bootTime).TotalMinutes) <= FreezeJoinWindowMinutes)
            .OrderBy(u => Math.Abs((u.TimeCreated - bootTime).TotalMinutes))
            .FirstOrDefault();

        // No Kernel-Power 41 at all near this boot, or it carried a real bugcheck code (a genuine
        // BSOD, already fully explained by the Minidumps/BugCheckRecord cards elsewhere on this
        // tab) - not what item 68 is looking for.
        if (nearest is null || nearest.BugcheckCode is not null)
            return (null, new List<string>());

        bool hasNearbyDump = false;
        try
        {
            if (Directory.Exists(MinidumpParserService.MinidumpFolder))
            {
                hasNearbyDump = Directory.GetFiles(MinidumpParserService.MinidumpFolder, "*.dmp")
                    .Any(f => Math.Abs((File.GetLastWriteTime(f) - nearest.TimeCreated).TotalHours) <= FreezeDumpJoinWindowHours);
            }
        }
        catch
        {
            // Can't enumerate the dump folder - degrade to "no dump found" (per CLAUDE.md, a
            // missed dump-file check shouldn't block a label that's already "quick flag" territory).
        }
        if (hasNearbyDump) return (null, new List<string>());

        string label = nearest.Cause switch
        {
            ShutdownCause.HardHang => "Freeze without crash — looks like a true hard hang: no bugcheck and no dump file were recorded before the system went silent.",
            ShutdownCause.PowerLoss => "Freeze without crash — looks like a sudden loss of power: no bugcheck and no dump file were recorded before the system went silent.",
            ShutdownCause.PowerButtonHeld => "Freeze without crash — the power button was held to recover from an unresponsive system: no bugcheck or dump file was recorded.",
            _ => "Freeze without crash: no bugcheck and no dump file were recorded before the system went silent.",
        };

        return (label, ReadEventsBeforeSilence(nearest.TimeCreated));
    }

    /// <summary>Item 68: the last handful of System-log events recorded at or before beforeTime -
    /// context for DetectFreezeWithoutCrash's label. A bounded, on-demand scan (this whole tab is
    /// queried on demand, not polled - see StabilityViewModel's own remarks) run only for boots
    /// already flagged as a probable freeze, so it stays cheap even though it isn't filtered by
    /// provider/event ID the way every other read in this file is.</summary>
    private static List<string> ReadEventsBeforeSilence(DateTime beforeTime)
    {
        var result = new List<string>();
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, "*") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            int scanned = 0;
            const int maxScan = 500; // bounded - newest-first, so this only walks forward from "now"
                                      // until it passes beforeTime, not the whole log.
            while (result.Count < FreezeContextEventCount && scanned < maxScan && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    scanned++;
                    var t = record.TimeCreated;
                    if (t is null || t.Value > beforeTime) continue; // still newer than the freeze marker itself

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    string summary = string.IsNullOrWhiteSpace(message) ? $"Event {record.Id}" : Truncate(message, 150);
                    result.Add($"{t:g}  [{record.ProviderName}] {summary}");
                }
            }
        }
        catch
        {
            // Provider/log unavailable - no "last events before the silence" context available.
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------
    // Round 13, item 7: dump-creation-failed detection.
    // ---------------------------------------------------------------------------------------

    /// <summary>Item 7: volmgr 161/162 "dump creation failed" events - explains the common "I had
    /// a BSOD but there's no dump file" case a bare Minidump-folder listing can't. NtStatus is a
    /// best-effort regex pull of the first hex status code out of the event's own formatted
    /// message text (the legacy volmgr provider doesn't expose it as a separate named property).</summary>
    private static List<DumpFailureEvent> ReadDumpFailures()
    {
        var result = new List<DumpFailureEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='volmgr'] and (EventID=161 or EventID=162) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 60;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    var statusMatch = Regex.Match(message, @"0x[0-9A-Fa-f]{8}");
                    result.Add(new DumpFailureEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        EventId = record.Id,
                        NtStatus = statusMatch.Success ? statusMatch.Value : null,
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "no dump-failure events found".
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------
    // Round 13, items 9/10: WHEA hardware-error log + partial binary record decode.
    // ---------------------------------------------------------------------------------------

    /// <summary>Item 9: Microsoft-Windows-WHEA-Logger events 17/18/19/47 - corrected and
    /// uncorrectable machine-check, memory and PCIe errors. Severity/Source are pulled from the
    /// event's own formatted message text; Decoded is item 10's best-effort partial decode of the
    /// binary ErrorRecord blob attached to the event, when one is present in this event's
    /// properties (see DecodeWheaErrorRecord).</summary>
    private static List<WheaErrorEvent> ReadWheaErrors()
    {
        var result = new List<WheaErrorEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (EventID=17 or EventID=18 or EventID=19 or EventID=47) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 300;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    string severity = ExtractWheaSeverity(message);
                    string source = ExtractWheaSource(message);

                    byte[]? blob = null;
                    try
                    {
                        foreach (var p in record.Properties)
                        {
                            if (p.Value is byte[] b && b.Length > 64) { blob = b; break; }
                        }
                    }
                    catch { /* leave blob null - Decoded falls back below */ }

                    result.Add(new WheaErrorEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        EventId = record.Id,
                        Severity = severity,
                        Source = source,
                        Decoded = blob is not null ? DecodeWheaErrorRecord(blob) : $"{severity} hardware error (raw error record unavailable)",
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "no WHEA hardware errors found".
        }
        return result;
    }

    private static string ExtractWheaSeverity(string message)
    {
        if (message.IndexOf("Fatal", StringComparison.OrdinalIgnoreCase) >= 0) return "Fatal";
        if (message.IndexOf("Uncorrectable", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("Uncorrected", StringComparison.OrdinalIgnoreCase) >= 0) return "Uncorrectable";
        if (message.IndexOf("Corrected", StringComparison.OrdinalIgnoreCase) >= 0) return "Corrected";
        return "Unknown";
    }

    private static string ExtractWheaSource(string message)
    {
        var match = Regex.Match(message, @"Error Source:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value.Trim();

        match = Regex.Match(message, @"reported by component:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "Unknown";
    }

    // Section-type GUIDs from the UEFI Common Platform Error Record (CPER) spec - the binary
    // format WHEA uses for the ErrorRecord blob attached to these events. This is the well-known,
    // stable part of the format; only these seven of the many section types the spec defines are
    // mapped, since they're the ones a home/desktop crash investigation actually cares about.
    private static readonly Dictionary<Guid, string> WheaSectionTypes = new()
    {
        [new Guid("9876CCAD-47B4-4bdb-B65E-16F193C4F3DB")] = "Processor (machine-check) error",
        [new Guid("A5BC1114-6F64-4EDE-B863-3E83ED7C83B1")] = "Memory error",
        [new Guid("D995E954-BBC1-430F-AD91-B44DCB3C6F35")] = "PCI Express error",
        [new Guid("C5753963-3B84-4095-BF78-EDDAD3F9C9DD")] = "PCI/PCI-X bus error",
        [new Guid("EB5E4685-CA66-4769-B6A2-26068B001326")] = "PCI/PCI-X device error",
        [new Guid("81212A96-09ED-4996-9471-8D729C8E69ED")] = "Firmware error record reference",
        [new Guid("5B51FEF7-C79D-4434-8F1B-AA62DE3E2C64")] = "DMAr error",
    };

    /// <summary>
    /// Item 10: best-effort, PARTIAL decode of the WHEA binary ErrorRecord blob (the UEFI CPER -
    /// Common Platform Error Record - format WHEA uses). Only two things are decoded with real
    /// confidence: the fixed 128-byte common header (signature check + severity + section count)
    /// and the first section descriptor's SectionType GUID, mapped to a friendly name via
    /// <see cref="WheaSectionTypes"/> (the UEFI spec's own well-known GUID list). Per-section field
    /// offsets (a memory section's physical address, a PCIe section's bus/device/function) are also
    /// attempted below, per the UEFI spec's published layout, but are NOT exhaustively validated
    /// against every vendor/firmware's real-world output - a decode that produces an out-of-range
    /// or implausible value silently falls back to just the section-type name, per this project's
    /// "degrade to Unknown, never fabricate" convention (CLAUDE.md), rather than showing a wrong
    /// address/device as if it were confirmed. This is intentionally not a full CPER decoder.
    /// </summary>
    private static string DecodeWheaErrorRecord(byte[] blob)
    {
        try
        {
            const int headerSize = 128;
            if (blob.Length < headerSize + 72) return "Unknown hardware error section";

            uint signature = BitConverter.ToUInt32(blob, 0);
            if (signature != 0x52455043) return "Unknown hardware error section"; // "CPER" magic

            ushort sectionCount = BitConverter.ToUInt16(blob, 14);
            uint severity = BitConverter.ToUInt32(blob, 16);
            string severityText = severity switch
            {
                0 => "Recoverable",
                1 => "Fatal",
                2 => "Corrected",
                3 => "None",
                _ => "Unknown-severity",
            };

            if (sectionCount == 0) return $"{severityText} hardware error (no section descriptors present)";

            // The first section descriptor starts immediately after the 128-byte header; per the
            // CPER spec its SectionType GUID sits at offset 20 within the fixed 72-byte descriptor,
            // and its own Offset/Length fields (pointing at the section's data, elsewhere in the
            // blob) sit at offsets 0 and 4.
            int descriptorOffset = headerSize;
            int sectionTypeOffset = descriptorOffset + 20;
            if (blob.Length < sectionTypeOffset + 16) return $"{severityText} hardware error";

            var typeBytes = new byte[16];
            Array.Copy(blob, sectionTypeOffset, typeBytes, 0, 16);
            var sectionType = new Guid(typeBytes);

            if (!WheaSectionTypes.TryGetValue(sectionType, out var sectionName))
                return $"{severityText} hardware error (unrecognized section type {sectionType})";

            uint sectionOffset = BitConverter.ToUInt32(blob, descriptorOffset + 0);
            uint sectionLength = BitConverter.ToUInt32(blob, descriptorOffset + 4);

            if (sectionType == new Guid("A5BC1114-6F64-4EDE-B863-3E83ED7C83B1"))
            {
                var addr = TryDecodeMemoryAddress(blob, sectionOffset, sectionLength);
                return addr is { } a ? $"{severityText} memory error — physical address 0x{a:X}" : $"{severityText} {sectionName}";
            }
            if (sectionType == new Guid("D995E954-BBC1-430F-AD91-B44DCB3C6F35"))
            {
                var bdf = TryDecodePcieBdf(blob, sectionOffset, sectionLength);
                return bdf is not null ? $"{severityText} PCI Express error — device {bdf}" : $"{severityText} {sectionName}";
            }

            return $"{severityText} {sectionName}";
        }
        catch
        {
            return "Unknown hardware error section";
        }
    }

    /// <summary>Best-effort CPER Memory Error section decode: ValidationBits (u64 @ offset 0) bit 2
    /// = "physical address valid" per the spec's own bit assignment; PhysicalAddress itself sits at
    /// offset 16. Not re-verified against every firmware's real output, hence "best-effort".</summary>
    private static ulong? TryDecodeMemoryAddress(byte[] blob, uint sectionOffset, uint sectionLength)
    {
        try
        {
            if (sectionOffset == 0 || sectionLength < 24) return null;
            if (sectionOffset + 24 > blob.Length) return null;

            ulong validationBits = BitConverter.ToUInt64(blob, (int)sectionOffset + 0);
            ulong physicalAddress = BitConverter.ToUInt64(blob, (int)sectionOffset + 16);

            if ((validationBits & 0x4) == 0) return null; // address not marked valid
            if (physicalAddress == 0 || physicalAddress == ulong.MaxValue) return null; // implausible
            return physicalAddress;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Best-effort CPER PCI Express Error section decode: pulls Segment/Bus/Device/
    /// Function out of the section's DeviceId sub-structure per the spec's published offsets. An
    /// all-zero or all-0xFF result (the common "nothing actually decoded meaningfully" shape) falls
    /// back to null rather than reporting a device that almost certainly isn't real.</summary>
    private static string? TryDecodePcieBdf(byte[] blob, uint sectionOffset, uint sectionLength)
    {
        try
        {
            if (sectionOffset == 0 || sectionLength < 40) return null;
            if (sectionOffset + 40 > blob.Length) return null;

            byte function = blob[(int)sectionOffset + 31];
            byte device = blob[(int)sectionOffset + 32];
            ushort segment = BitConverter.ToUInt16(blob, (int)sectionOffset + 33);
            byte bus = blob[(int)sectionOffset + 35];

            if (segment == 0xFFFF) return null;
            if (bus == 0 && device == 0 && function == 0) return null;

            return $"{segment:X4}:{bus:X2}:{device:X2}.{function:X1}";
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Round 13, item 11: Reliability Monitor's own WMI stability index.
    // ---------------------------------------------------------------------------------------

    /// <summary>Item 11: Win32_ReliabilityStabilityMetrics (root\CIMV2) - the same WMI class
    /// backing Windows' own Reliability Monitor, giving Microsoft's per-day 0-10 stability index.
    /// A day with no data reports SystemStabilityIndex as -1 (or is simply absent); both are
    /// excluded here rather than plotted as a fabricated 0.</summary>
    private static List<ReliabilityMetricPoint> ReadReliabilityMetrics()
    {
        var result = new List<ReliabilityMetricPoint>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2", "SELECT TimeGenerated, SystemStabilityIndex FROM Win32_ReliabilityStabilityMetrics");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    try
                    {
                        var timeGenerated = mo["TimeGenerated"] as string;
                        var indexObj = mo["SystemStabilityIndex"];
                        if (string.IsNullOrEmpty(timeGenerated) || indexObj is null) continue;

                        var dt = ManagementDateTimeConverter.ToDateTime(timeGenerated);
                        double idx = Convert.ToDouble(indexObj);
                        if (idx < 0) continue; // no data for that day

                        result.Add(new ReliabilityMetricPoint { Date = dt.Date, Index = idx });
                    }
                    catch { /* one malformed row shouldn't stop the rest of the scan */ }
                }
            }
        }
        catch
        {
            // Namespace/class unavailable - degrade to "no Microsoft reliability index available".
        }
        return result.OrderBy(r => r.Date).ToList();
    }

    // ---------------------------------------------------------------------------------------
    // Round 13, item 12: event-log health / coverage check.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Item 12: "is the 30-day lookback window even trustworthy" check - a log that was cleared
    /// recently, or is small enough that its actual retention doesn't cover the full lookback
    /// window, means a clean "no crashes found" result elsewhere on this tab can be a hollow one.
    /// OldestRecordTime is read directly off the System log's own oldest record (a single forward
    /// EventLogReader.ReadEvent() call - cheap, since it only reads one record). MaxSizeBytes comes
    /// from `wevtutil gl System` (shelled out, matching VolumeDiagnosticsService's established
    /// "known tool, redirect stdout, bounded wait, kill on timeout" pattern). WasClearedRecently/
    /// LastClearedTime come from System-log event 104 (Eventlog provider, "The System log file was
    /// cleared").
    /// </summary>
    private static EventLogHealth ReadLogHealth()
    {
        DateTime? oldest = null;
        long? maxSize = null;
        bool wasCleared = false;
        DateTime? lastCleared = null;

        try
        {
            var query = new EventLogQuery("System", PathType.LogName, "*") { ReverseDirection = false };
            using var reader = new EventLogReader(query);
            using var record = reader.ReadEvent();
            oldest = record?.TimeCreated;
        }
        catch
        {
            // Log unavailable/access denied - "oldest record unknown".
        }

        try
        {
            var psi = new ProcessStartInfo("wevtutil.exe", "gl System")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var outputTask = proc.StandardOutput.ReadToEndAsync();
                var errorTask = proc.StandardError.ReadToEndAsync();
                if (proc.WaitForExit(5000))
                {
                    string output = outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult();
                    var sizeMatch = Regex.Match(output, @"maxSize:\s*(\d+)", RegexOptions.IgnoreCase);
                    if (sizeMatch.Success && long.TryParse(sizeMatch.Groups[1].Value, out var sz)) maxSize = sz;
                }
                else
                {
                    try { proc.Kill(); } catch { /* best-effort */ }
                }
            }
        }
        catch
        {
            // wevtutil unavailable - "max size unknown".
        }

        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Eventlog'] and (EventID=104) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };
            using var reader = new EventLogReader(query);
            if (reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    lastCleared = record.TimeCreated;
                    wasCleared = true;
                }
            }
        }
        catch
        {
            // Provider/log unavailable - "not known to have been cleared recently".
        }

        return new EventLogHealth
        {
            OldestRecordTime = oldest,
            MaxSizeBytes = maxSize,
            WasClearedRecently = wasCleared,
            LastClearedTime = lastCleared,
        };
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


    // ---------------------------------------------------------------------------------------
    // Round 16, item 47: Application-log event 1000 (Application Error) - joined to a WER report
    // by app/module name (see WerReportService.JoinApplicationErrorEvents). A caller-supplied
    // lookback rather than the fixed LookbackDays constant every other read in this file uses,
    // since WER archive folders (item 48) commonly outlive the 30-day window everything else on
    // this tab is capped to, and this read exists specifically to join against those.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Round 16, item 47 / Round 17, item 50 (this chunk's anchor): reads the "Application Error"
    /// provider's event 1000 and parses every documented positional insertion string - faulting
    /// application name/version/timestamp (0-2), faulting module name/version/timestamp (3-5),
    /// exception code (6), fault offset (7), process id (8), application start time (9, not
    /// surfaced - not useful on its own without the process's own launch context), application
    /// path (10), module path (11), report id (12) - defensively bounds-checked like every other
    /// legacy-provider parse in this file, since it isn't a formally versioned per-Windows-release
    /// contract either. Every other item in this chunk (51/52/56/57/60) is a view, lookup or join
    /// over this same parsed list - see ApplicationCrashService and StabilityViewModel, which both
    /// call this once per refresh rather than re-querying per item.
    /// </summary>
    public List<ApplicationCrashEvent> ReadApplicationCrashEvents(int lookbackDays)
    {
        var result = new List<ApplicationCrashEvent>();
        // Item 69: read once up front and join per-event below - the same sleep/resume cross-
        // reference item 33 originally built for 0x9F bugchecks only, generalized to every crash/
        // hang row on this tab (see ApplicationCrashEvent.HappenedDuringSleepResume's remarks).
        var sleepResumeEvents = ReadSleepResumeEventTimes();
        try
        {
            long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='Application Error'] and (EventID=1000) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 500;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    try
                    {
                        var props = record.Properties;
                        string? Get(int idx) => idx < props.Count ? (props[idx].Value as string ?? props[idx].Value?.ToString()) : null;
                        string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

                        string? appName = Norm(Get(0));
                        string? appVersion = Norm(Get(1));
                        string? appTimeStamp = Norm(Get(2));
                        string? modName = Norm(Get(3));
                        string? modVersion = Norm(Get(4));
                        string? modTimeStamp = Norm(Get(5));
                        string? exceptionCode = Norm(Get(6));
                        string? offset = Norm(Get(7));
                        string? processIdRaw = Norm(Get(8));
                        string? appPath = Norm(Get(10));
                        string? modPath = Norm(Get(11));
                        string? reportId = Norm(Get(12));

                        int? pid = null;
                        if (processIdRaw is not null)
                        {
                            string trimmed = processIdRaw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? processIdRaw[2..] : processIdRaw;
                            if (int.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var hexPid))
                                pid = hexPid;
                            else if (int.TryParse(processIdRaw, out var decPid))
                                pid = decPid;
                        }

                        string message;
                        try { message = record.FormatDescription() ?? string.Empty; }
                        catch { message = string.Empty; }

                        var timeCreated = record.TimeCreated ?? DateTime.MinValue;
                        result.Add(new ApplicationCrashEvent
                        {
                            TimeCreated = timeCreated,
                            HappenedDuringSleepResume = sleepResumeEvents.Any(t =>
                                Math.Abs((t - timeCreated).TotalMinutes) <= SleepResumeJoinWindowMinutes),
                            AppName = appName,
                            AppVersion = appVersion,
                            AppTimeStamp = appTimeStamp,
                            ModName = modName,
                            ModVersion = modVersion,
                            ModTimeStamp = modTimeStamp,
                            ExceptionCode = exceptionCode,
                            // Item 51: plain-English exception name, computed once here rather
                            // than live in the model (Models don't call into Services - see
                            // ApplicationCrashEvent's own remarks).
                            ExceptionCodeText = NtStatusLookup.Describe(exceptionCode),
                            Offset = offset,
                            ProcessId = pid,
                            ApplicationPath = appPath,
                            ModulePath = modPath,
                            ReportId = reportId,
                            Message = Truncate(message, 300),
                        });
                    }
                    catch { /* one malformed record shouldn't stop the rest of the scan */ }
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "no Application Error events available"
            // (item 47's WER join just falls back to showing each report on its own).
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------
    // Round 17, item 53: Application-log event 1002 ("Application Hang") - a separate fault
    // class from event 1000 above. Parsed from the event's own formatted message text (regex,
    // like several other legacy-provider parses in this file already do) rather than positional
    // properties, since this event's raw property layout isn't a documented, versioned contract
    // and the message text's own labelled fields are far more stable to key off. HangType/
    // HangSignature are deliberately NOT parsed here - see ApplicationHangEvent's remarks on why
    // those are joined in from the matching WER AppHang report instead (WerReportService.
    // JoinApplicationHangEvents).
    // ---------------------------------------------------------------------------------------

    private static readonly Regex AppHangProgramRegex = new(
        @"^The program\s+(.+?)\s+version\s+(.+?)\s+stopped interacting", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex AppHangPidRegex = new(@"Process ID:\s*(0x[0-9A-Fa-f]+|\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AppHangPathRegex = new(@"Application Path:\s*([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AppHangReportIdRegex = new(@"Report Id:\s*([0-9A-Fa-f-]{20,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<ApplicationHangEvent> ReadApplicationHangEvents(int lookbackDays)
    {
        var result = new List<ApplicationHangEvent>();
        // Item 69: same sleep/resume cross-reference as ReadApplicationCrashEvents above.
        var sleepResumeEvents = ReadSleepResumeEventTimes();
        try
        {
            long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='Application Hang'] and (EventID=1002) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 300;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    try
                    {
                        string message;
                        try { message = record.FormatDescription() ?? string.Empty; }
                        catch { message = string.Empty; }

                        string? processName = null, version = null;
                        var programMatch = AppHangProgramRegex.Match(message);
                        if (programMatch.Success)
                        {
                            processName = programMatch.Groups[1].Value.Trim();
                            version = programMatch.Groups[2].Value.Trim();
                        }

                        string? pid = AppHangPidRegex.Match(message) is { Success: true } pm ? pm.Groups[1].Value.Trim() : null;
                        string? path = AppHangPathRegex.Match(message) is { Success: true } pathm ? pathm.Groups[1].Value.Trim() : null;
                        string? reportId = AppHangReportIdRegex.Match(message) is { Success: true } rm ? rm.Groups[1].Value.Trim() : null;

                        var timeCreated = record.TimeCreated ?? DateTime.MinValue;
                        result.Add(new ApplicationHangEvent
                        {
                            TimeCreated = timeCreated,
                            HappenedDuringSleepResume = sleepResumeEvents.Any(t =>
                                Math.Abs((t - timeCreated).TotalMinutes) <= SleepResumeJoinWindowMinutes),
                            ProcessName = processName,
                            Version = version,
                            ProcessId = pid,
                            ApplicationPath = string.IsNullOrWhiteSpace(path) ? null : path,
                            ReportId = string.IsNullOrWhiteSpace(reportId) ? null : reportId,
                            Message = Truncate(message, 300),
                        });
                    }
                    catch { /* one malformed record shouldn't stop the rest of the scan */ }
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "no application hangs found".
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------
    // Round 17, item 54: ".NET Runtime" provider events 1026 (unhandled exception) and 1023 -
    // both carry the same "Application: X / Framework Version: Y / Exception Info: Type: message"
    // text shape followed by a managed stack trace ("   at ..." lines per frame), so both are
    // read with the same regex-based parse against the formatted message text - the message text
    // is a long-stable, widely-documented shape for this provider, unlike this file's other
    // legacy-provider parses that fall back to regex only because the *positional* layout isn't
    // documented.
    // ---------------------------------------------------------------------------------------

    private static readonly Regex ClrAppNameRegex = new(@"^Application:\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex ClrFrameworkVersionRegex = new(@"^Framework Version:\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex ClrExceptionInfoLineRegex = new(@"^Exception Info:\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex ClrStackFrameRegex = new(@"^\s{3}at\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    public List<ManagedExceptionEvent> ReadClrExceptionEvents(int lookbackDays)
    {
        var result = new List<ManagedExceptionEvent>();
        try
        {
            long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='.NET Runtime'] and (EventID=1026 or EventID=1023) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 300;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    try
                    {
                        string message;
                        try { message = record.FormatDescription() ?? string.Empty; }
                        catch { message = string.Empty; }

                        string? appName = ClrAppNameRegex.Match(message) is { Success: true } am ? am.Groups[1].Value.Trim() : null;
                        string? framework = ClrFrameworkVersionRegex.Match(message) is { Success: true } fm ? fm.Groups[1].Value.Trim() : null;

                        string? exceptionType = null, exceptionMessage = null;
                        var excMatch = ClrExceptionInfoLineRegex.Match(message);
                        if (excMatch.Success)
                        {
                            string line = excMatch.Groups[1].Value.Trim();
                            int colon = line.IndexOf(':');
                            if (colon > 0)
                            {
                                exceptionType = line[..colon].Trim();
                                exceptionMessage = line[(colon + 1)..].Trim();
                            }
                            else
                            {
                                exceptionType = line;
                            }
                        }

                        var frames = ClrStackFrameRegex.Matches(message)
                            .Select(m => m.Groups[1].Value.Trim())
                            .Take(5)
                            .ToList();

                        result.Add(new ManagedExceptionEvent
                        {
                            TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                            EventId = record.Id,
                            ApplicationName = string.IsNullOrWhiteSpace(appName) ? null : appName,
                            FrameworkVersion = string.IsNullOrWhiteSpace(framework) ? null : framework,
                            ExceptionType = string.IsNullOrWhiteSpace(exceptionType) ? null : exceptionType,
                            ExceptionMessage = string.IsNullOrWhiteSpace(exceptionMessage) ? null : exceptionMessage,
                            TopStackFrames = frames,
                            Message = Truncate(message, 500),
                        });
                    }
                    catch { /* one malformed record shouldn't stop the rest of the scan */ }
                }
            }
        }
        catch
        {
            // Provider/log unavailable (a machine that's never run/crashed a managed process) -
            // degrade to "no managed exceptions found".
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------
    // Round 17, item 58: Service Control Manager crash/failure events - the service-side
    // equivalent of an application crash. Each event id has its own known property layout
    // (documented by Microsoft, though - like every other legacy-provider parse in this file -
    // not a formally versioned per-release contract), read positionally and defensively bounds-
    // checked.
    // ---------------------------------------------------------------------------------------

    public List<ServiceFailureEvent> ReadServiceFailureEvents(int lookbackDays)
    {
        var result = new List<ServiceFailureEvent>();
        try
        {
            long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Service Control Manager'] and (EventID=7031 or EventID=7034 or EventID=7024 or EventID=7000 or EventID=7009) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 500;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    var parsed = ParseServiceFailureEvent(record);
                    if (parsed is not null) result.Add(parsed);
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "no service failures found".
        }
        return result;
    }

    private static ServiceFailureEvent? ParseServiceFailureEvent(EventRecord record)
    {
        try
        {
            var props = record.Properties;
            string message;
            try { message = record.FormatDescription() ?? string.Empty; }
            catch { message = string.Empty; }

            DateTime time = record.TimeCreated ?? DateTime.MinValue;

            switch (record.Id)
            {
                // 7034: "The %1 service terminated unexpectedly. It has done this %2 time(s)."
                // 7031: same, plus "The following corrective action will be taken in %3
                // milliseconds: %4."
                case 7034:
                case 7031:
                {
                    string? name = props.Count > 0 ? props[0].Value as string : null;
                    int? restartCount = props.Count > 1 && TryToInt(props[1].Value, out var c) ? c : null;
                    string? action = record.Id == 7031 && props.Count > 3 ? props[3].Value as string : null;
                    return new ServiceFailureEvent
                    {
                        TimeCreated = time,
                        EventId = record.Id,
                        ServiceName = string.IsNullOrWhiteSpace(name) ? null : name,
                        RestartCount = restartCount,
                        RecoveryAction = string.IsNullOrWhiteSpace(action) ? null : action,
                        Message = Truncate(message, 300),
                    };
                }
                // 7024: "The %1 service terminated with service-specific error %2."
                case 7024:
                {
                    string? name = props.Count > 0 ? props[0].Value as string : null;
                    string? exitCode = props.Count > 1 ? FormatBugcheckValue(props[1].Value) : null;
                    return new ServiceFailureEvent
                    {
                        TimeCreated = time,
                        EventId = 7024,
                        ServiceName = string.IsNullOrWhiteSpace(name) ? null : name,
                        ExitCode = exitCode,
                        Message = Truncate(message, 300),
                    };
                }
                // 7000: "The %1 service failed to start due to the following error: ..."
                case 7000:
                {
                    string? name = props.Count > 0 ? props[0].Value as string : null;
                    return new ServiceFailureEvent
                    {
                        TimeCreated = time,
                        EventId = 7000,
                        ServiceName = string.IsNullOrWhiteSpace(name) ? null : name,
                        Message = Truncate(message, 300),
                    };
                }
                // 7009: "A timeout was reached (%1 milliseconds) while waiting for the %2 service
                // to connect." - the service name is property index 1 here, not 0.
                case 7009:
                {
                    string? name = props.Count > 1 ? props[1].Value as string : null;
                    return new ServiceFailureEvent
                    {
                        TimeCreated = time,
                        EventId = 7009,
                        ServiceName = string.IsNullOrWhiteSpace(name) ? null : name,
                        Message = Truncate(message, 300),
                    };
                }
                default:
                    return null;
            }
        }
        catch
        {
            // One malformed record shouldn't stop the rest of the scan.
            return null;
        }
    }

    private static bool TryToInt(object? value, out int result)
    {
        result = 0;
        if (value is null) return false;
        try { result = Convert.ToInt32(value); return true; }
        catch { return false; }
    }

    // ---------------------------------------------------------------------------------------
    // Round 21, item 99: Windows Memory Diagnostic (mdsched.exe) result readback - mdsched itself
    // runs entirely outside Windows (before any OS loads) and only ever reports its own result
    // once, as a toast right after the triggering reboot finishes - easy to miss, and gone for
    // good once dismissed. The tool logs its own verdict as one event under the
    // Microsoft-Windows-MemoryDiagnostics-Results provider on the System log (1201 = no problems
    // found, 1101 = hardware errors were detected), so reading it back here means the Stability
    // tab can show the answer any time after the fact, not just in the moment. Same
    // EventLogReader/EventLogQuery shape as every other targeted provider+ID read in this file.
    // ---------------------------------------------------------------------------------------

    private const string MemoryDiagnosticsProviderName = "Microsoft-Windows-MemoryDiagnostics-Results";
    private const int MemoryDiagnosticsNoErrorsEventId = 1201;
    private const int MemoryDiagnosticsErrorsFoundEventId = 1101;

    public List<Models.MemoryDiagnosticResultInfo> ReadMemoryDiagnosticsResults(int lookbackDays)
    {
        var result = new List<Models.MemoryDiagnosticResultInfo>();
        try
        {
            long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{MemoryDiagnosticsProviderName}'] and (EventID={MemoryDiagnosticsNoErrorsEventId} or EventID={MemoryDiagnosticsErrorsFoundEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 50; // mdsched is a manually-launched, occasional tool - never a busy log source
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    result.Add(new Models.MemoryDiagnosticResultInfo
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        HadErrors = record.Id == MemoryDiagnosticsErrorsFoundEventId,
                        ResultText = message.Length > 0
                            ? Truncate(message, 400)
                            : (record.Id == MemoryDiagnosticsErrorsFoundEventId
                                ? "Hardware errors were detected."
                                : "No memory problems were detected."),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "no Memory Diagnostic results found", the same
            // as every other provider-scoped read in this file (a normal outcome when mdsched has
            // never been run on this machine).
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

    #region #759 - Service start-type change audit (event 7040)

    /// <summary>
    /// #759: System-log Service Control Manager event 7040 ("The start type of the %1 service was
    /// changed from %2 to %3.") - unlike the #749 failure-event family, 7040's properties are stable
    /// and indexed (verified live: [0]=display name, [1]=old start type, [2]=new start type,
    /// [3]=service name), so no message-text regex is needed here. 7040 does not record which
    /// account made the change - that needs Security-log object-access auditing, a separate audit
    /// policy this app doesn't turn on, so no Account field is fabricated (see
    /// ServiceStartTypeChangeEvent's remarks).
    /// </summary>
    public List<ServiceStartTypeChangeEvent> ReadStartTypeChangeEvents()
    {
        var result = new List<ServiceStartTypeChangeEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Service Control Manager'] and (EventID=7040) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 1000;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is null || record.Properties.Count < 4) continue;
                    try
                    {
                        result.Add(new ServiceStartTypeChangeEvent
                        {
                            TimeCreated = record.TimeCreated.Value,
                            DisplayName = record.Properties[0].Value as string ?? string.Empty,
                            OldStartType = record.Properties[1].Value as string ?? string.Empty,
                            NewStartType = record.Properties[2].Value as string ?? string.Empty,
                            ServiceName = record.Properties[3].Value as string ?? string.Empty,
                        });
                    }
                    catch { /* one malformed entry shouldn't stop the rest of the scan */ }
                }
            }
        }
        catch
        {
            // Log unavailable/access denied - degrade to "no configuration-change history", same as
            // every other event-log read in this service.
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    #endregion

    #region #760 - Newly installed service and driver log (event 7045)

    /// <summary>
    /// #760: System-log Service Control Manager event 7045 ("A service was installed in the
    /// system.") - also stable and indexed (verified live: [0]=service name, [1]=service file name
    /// (quoted image path), [2]=service type, [3]=service start type, [4]=service account).
    /// SignatureStatus is left "Unknown" here - correlated in afterward by the caller
    /// (StartupViewModel/ServicesViewModel), since a signature check reads the file from disk and
    /// this method should stay a pure event-log read.
    /// </summary>
    public List<NewServiceInstallEvent> ReadNewServiceInstallEvents()
    {
        var result = new List<NewServiceInstallEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Service Control Manager'] and (EventID=7045) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 1000;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is null || record.Properties.Count < 5) continue;
                    try
                    {
                        string imagePath = (record.Properties[1].Value as string ?? string.Empty).Trim().Trim('"');
                        result.Add(new NewServiceInstallEvent
                        {
                            TimeCreated = record.TimeCreated.Value,
                            ServiceName = record.Properties[0].Value as string ?? string.Empty,
                            ImagePath = imagePath,
                            ServiceType = record.Properties[2].Value as string ?? string.Empty,
                            StartType = record.Properties[3].Value as string ?? string.Empty,
                            Account = record.Properties[4].Value as string ?? string.Empty,
                        });
                    }
                    catch { /* one malformed entry shouldn't stop the rest of the scan */ }
                }
            }
        }
        catch
        {
            // Log unavailable/access denied - degrade to "nothing new found", same as every other
            // event-log read in this service.
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    #endregion

    #region #764/#768 - Task Scheduler operational log

    private const string TaskSchedulerOperationalLog = "Microsoft-Windows-TaskScheduler/Operational";

    /// <summary>#764: Microsoft-Windows-TaskScheduler/Operational is disabled by default on a stock
    /// Windows install - checked via EventLogConfiguration (a supported .NET API, no shell-out
    /// needed for a read-only check) so the Startup tab can offer a one-click enable rather than
    /// just silently returning an empty failure list that looks like "no failures" instead of
    /// "nothing was ever recorded".</summary>
    public static bool IsTaskSchedulerOperationalLogEnabled()
    {
        try
        {
            using var config = new EventLogConfiguration(TaskSchedulerOperationalLog);
            return config.IsEnabled;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>#764: `wevtutil sl "Microsoft-Windows-TaskScheduler/Operational" /e:true` - shells to
    /// wevtutil rather than EventLogConfiguration.IsEnabled/SaveChanges() so the confirmation dialog
    /// this is gated behind (CLAUDE.md's "mutating actions require confirmation with the exact
    /// command shown") has a literal command line to display, the same pattern every other
    /// mutating action in this app already follows. Never enabled silently - see
    /// StartupViewModel.EnableTaskFailureLogAsync.</summary>
    public static async Task<(bool Success, string? Error)> EnableTaskSchedulerOperationalLogAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("wevtutil.exe", $"sl \"{TaskSchedulerOperationalLog}\" /e:true")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "couldn't run wevtutil.exe");

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(10000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return (false, "wevtutil timed out");
            }

            string output = (await outputTask) + (await errorTask);
            return proc.ExitCode == 0 ? (true, null) : (false, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static readonly int[] TaskFailureEventIds = { 101, 103, 111, 203, 322, 332 };
    private static readonly Regex QuotedTaskNameRegex = new(@"""([^""]+)""", RegexOptions.Compiled);

    /// <summary>Every Task Scheduler operational event template quotes the affected task's path as
    /// its first quoted segment - the same "extract from the rendered message, not an indexed
    /// property" approach ScmServiceNamePatterns already takes for the #749 failure-event family,
    /// used here instead of guessing at an unverified manifest property order.</summary>
    private static string ExtractTaskSchedulerTaskName(string message)
    {
        var m = QuotedTaskNameRegex.Match(message);
        return m.Success ? m.Groups[1].Value.Trim() : "(unknown task)";
    }

    /// <summary>
    /// #764: scans the Task Scheduler operational channel for the failure-family event IDs (101
    /// task start failed, 103 action start failed, 111 terminated due to timeout, 203 action failed
    /// with a return code, 322 not run - instance already running, 332 not run - credential
    /// problem). Degrades to an empty list (not an exception) when the channel is disabled or
    /// doesn't exist yet - see IsTaskSchedulerOperationalLogEnabled for telling that state apart
    /// from "enabled, but genuinely nothing failed" in the UI.
    /// </summary>
    public List<TaskSchedulerOperationalEvent> ReadTaskFailureEvents()
    {
        var result = new List<TaskSchedulerOperationalEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            string idFilter = string.Join(" or ", TaskFailureEventIds.Select(id => $"EventID={id}"));
            var query = new EventLogQuery(TaskSchedulerOperationalLog, PathType.LogName,
                $"*[System[({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
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
                    if (record.TimeCreated is null) continue;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - same known gap ReadLog already handles

                    result.Add(new TaskSchedulerOperationalEvent
                    {
                        TimeCreated = record.TimeCreated.Value,
                        EventId = record.Id,
                        TaskName = ExtractTaskSchedulerTaskName(message),
                        Message = Truncate(message, 300),
                    });
                }
            }
        }
        catch
        {
            // Channel disabled/unavailable/access denied - degrade to "no failure history", same as
            // every other event-log read in this service.
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    private static readonly int[] TaskStartEventIds = { 100, 129 };
    private static readonly int[] TaskCompleteEventIds = { 102, 201 };

    /// <summary>
    /// #768: pairs Task Scheduler operational start events (100 task started, 129 action started)
    /// with their matching completion events (102 task completed, 201 action completed) by the
    /// event log's own ActivityId correlation GUID - the mechanism Task Scheduler itself uses to tie
    /// every event belonging to one task-instance run together, not a text-parsed "instance ID"
    /// (which isn't reliably at a stable message position across this event set, the same reasoning
    /// ScmServiceNamePatterns' remarks already give for a different event family). Reads oldest-
    /// first (not reversed) so a completion is naturally seen after the start it pairs with. A gap
    /// wider than 24h is discarded as implausible rather than reported as a wildly inflated
    /// duration, the same "sanity ceiling" ReadServiceStartDurations already applies to its own
    /// stopped-to-running pairing.
    /// </summary>
    public List<TaskRunDuration> ReadTaskRunDurations()
    {
        var starts = new Dictionary<Guid, (DateTime Time, string TaskName)>();
        var durations = new List<TaskRunDuration>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var allIds = TaskStartEventIds.Concat(TaskCompleteEventIds);
            string idFilter = string.Join(" or ", allIds.Select(id => $"EventID={id}"));
            var query = new EventLogQuery(TaskSchedulerOperationalLog, PathType.LogName,
                $"*[System[({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]");

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 5000;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is null || record.ActivityId is not { } activityId) continue;

                    if (TaskStartEventIds.Contains(record.Id))
                    {
                        string message;
                        try { message = record.FormatDescription() ?? string.Empty; } catch { message = string.Empty; }
                        starts[activityId] = (record.TimeCreated.Value, ExtractTaskSchedulerTaskName(message));
                    }
                    else if (TaskCompleteEventIds.Contains(record.Id) && starts.TryGetValue(activityId, out var start))
                    {
                        var elapsed = record.TimeCreated.Value - start.Time;
                        if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(24))
                        {
                            durations.Add(new TaskRunDuration
                            {
                                TaskName = start.TaskName,
                                StartTime = start.Time,
                                DurationMs = elapsed.TotalMilliseconds,
                            });
                        }
                        starts.Remove(activityId);
                    }
                }
            }
        }
        catch
        {
            // Channel disabled/unavailable - degrade to "no duration history".
        }
        return durations;
    }

    #endregion

    // Round 13, #122: the event knowledge base's "seriously bad" set can include Warning-level IDs
    // (disk 153, Ntfs 98 - exactly the "Windows' own levels lie" cases #120 is about), which the
    // fixed Level=1|2 sweep ReadLog/Query use above never reads at all - so the Stability tab's
    // "Known-bad IDs present on this PC" scorecard needs its own light, explicitly-scoped query
    // instead of reusing RecentEvents. "Light" here means an XPath that names the exact
    // provider+eventId pairs to look for (built from whatever the caller's knowledge base flags as
    // serious), not a second full Level-based sweep - still on-demand only, folded into
    // StabilityViewModel's existing RefreshCommand, not a new timer.
    public List<KnownBadIdScanHit> ScanForKnownBadIds(IReadOnlyCollection<(string Provider, int EventId)> flaggedIds, int lookbackDays = LookbackDays)
    {
        var hits = new Dictionary<(string Provider, int EventId), (int Count, DateTime LastSeen)>();
        if (flaggedIds.Count == 0) return new List<KnownBadIdScanHit>();

        string idsClause = string.Join(" or ", flaggedIds
            .GroupBy(f => f.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"(Provider[@Name='{EscapeXPathLiteral(g.Key)}'] and ({string.Join(" or ", g.Select(f => $"EventID={f.EventId}"))}))"));

        long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
        string xpath = $"*[System[({idsClause}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]";

        foreach (var logName in new[] { "System", "Application" })
        {
            try
            {
                var query = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                int count = 0;
                const int maxEvents = 2000; // generous cap - this is a narrow, ID-scoped query, not a full-log sweep
                while (count < maxEvents && reader.ReadEvent() is { } record)
                {
                    using (record)
                    {
                        count++;
                        var key = (record.ProviderName ?? string.Empty, record.Id);
                        var time = record.TimeCreated ?? DateTime.MinValue;
                        if (hits.TryGetValue(key, out var existing))
                            hits[key] = (existing.Count + 1, time > existing.LastSeen ? time : existing.LastSeen);
                        else
                            hits[key] = (1, time);
                    }
                }
            }
            catch
            {
                // This log unavailable/access denied - contribute nothing from it, keep scanning the other.
            }
        }

        return hits.Select(kv => new KnownBadIdScanHit
        {
            Provider = kv.Key.Provider,
            EventId = kv.Key.EventId,
            Count = kv.Value.Count,
            LastSeen = kv.Value.LastSeen,
        }).ToList();
    }

    /// <summary>A provider name containing a literal single quote would break the XPath string
    /// literal it's wrapped in - none of the bundled knowledge-base provider names do, but this
    /// strips one defensively rather than producing an unparseable query for a user-added override
    /// entry with an unusual provider name.</summary>
    private static string EscapeXPathLiteral(string value) => value.Replace("'", "");
}
