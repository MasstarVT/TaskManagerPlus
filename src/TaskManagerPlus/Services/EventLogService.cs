using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
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

        // Round 13: items 1/2/8 (authoritative bugcheck record + WER join + WER-summary fallback),
        // 3/4 (classified unexpected-shutdown history), 5/6 (shutdown/restart/boot timeline), 7
        // (dump-creation-failed detection), 9/10 (WHEA hardware errors + partial record decode), 11
        // (Microsoft's own Reliability Monitor index), 12 (log-coverage health check). Each is its
        // own independently-wrapped query/read, same "one provider/source, degrade to empty on any
        // failure" shape as every existing method in this file.
        var bugChecks = ReadBugCheckRecords();
        var unexpectedShutdowns = ReadUnexpectedShutdowns();

        return new StabilitySnapshot
        {
            RecentEvents = events.Take(MaxEventsPerLog).ToList(),
            WasLastShutdownUnexpected = wasUnexpected,
            LastUnexpectedShutdown = mostRecentShutdownEvent?.TimeCreated,
            TdrEventCount = tdrEvents.Count,
            LastTdrEvent = tdrEvents.FirstOrDefault()?.TimeCreated,
            LastCrashTime = lastCrash?.TimeCreated,
            Minidumps = ReadMinidumps(shutdownEvents, bugChecks),
            DailyCounts = BuildDailyCounts(events),
            LowMemoryEventCount = lowMemCount,
            LastLowMemoryEvent = lowMemLast,
            LatestBugCheck = bugChecks.FirstOrDefault(),
            LastShutdownCause = unexpectedShutdowns.OrderByDescending(u => u.TimeCreated).FirstOrDefault()?.Cause,
            UnexpectedShutdowns = unexpectedShutdowns,
            ShutdownTimeline = ReadShutdownTimeline(),
            DumpFailures = ReadDumpFailures(),
            WheaErrors = ReadWheaErrors(),
            ReliabilityMetrics = ReadReliabilityMetrics(),
            LogHealth = ReadLogHealth(),
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

    /// <summary>
    /// Round 13, item 1: for each minidump file, prefers the authoritative BugCheck 1001 record
    /// (see ReadBugCheckRecords) whose own dump path matches this file by name - it carries the
    /// real stop code, all four bugcheck parameters, and (item 2) a joined WER report, not just a
    /// best-effort nearby-timestamp guess. Only falls back to the old ±10-minute Kernel-Power-41
    /// timestamp correlation when no matching authoritative record was found (an older Windows
    /// version without the BugCheck provider, or a log that's already rolled the event off) -
    /// parsing the bugcheck code directly out of the .dmp binary format would need a full
    /// MINIDUMP-stream reader, a much larger and more fragile undertaking than either of these.
    /// </summary>
    private static List<MinidumpInfo> ReadMinidumps(List<StabilityEvent> shutdownEvents, List<BugCheckRecord> bugChecks)
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
    private static WerReportInfo? ResolveWerReport(string reportId)
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

                return new WerReportInfo
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
    /// </summary>
    private static List<ShutdownTimelineEntry> ReadShutdownTimeline()
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
            entries.Add(new ShutdownTimelineEntry
            {
                TimeCreated = boot,
                Kind = "Boot",
                IsDirtyBoot = precedingShutdown is null,
            });
            if (precedingShutdown is { } used) remainingShutdowns.Remove(used);
        }

        return entries.OrderByDescending(e => e.TimeCreated).Take(150).ToList();
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
}
