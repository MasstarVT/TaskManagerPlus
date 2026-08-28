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
        var correctedMemoryErrors = ReadCorrectedMemoryErrors();

        // #487/#488/#489/#490/#492: the broad WHEA-Logger read, plus the crash-like event list
        // (unexpected shutdown, BSOD report, and - unlike crashLikeIds above - also TDR) that #492
        // correlates it against.
        var wheaEvents = ReadWheaHardwareErrors();
        var crashTimelineIds = new HashSet<int> { KernelPowerEventId, LegacyUncleanShutdownEventId, BlueScreenEventId, TdrEventId };
        var crashTimelineEvents = events.Where(e => crashTimelineIds.Contains(e.EventId))
            .OrderByDescending(e => e.TimeCreated).ToList();

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
            PoolExhaustionEvents = ReadPoolExhaustionEvents(),
            OutOfMemoryIncidents = ReadOutOfMemoryIncidents(),
            // #447: corrected-memory-error events - also read independently (this same method) by
            // SystemSpecsService for the System Specs memory section, so the two tabs' figures stay
            // in sync without SystemSpecsViewModel needing a reference to StabilityViewModel.
            CorrectedMemoryErrorCount = correctedMemoryErrors.Count,
            LastCorrectedMemoryError = correctedMemoryErrors.Count > 0 ? correctedMemoryErrors[0].TimeCreated : null,
            CorrectedMemoryErrors = correctedMemoryErrors,
            // #451: memory-related bugcheck count, for the Stability tab's own display and for
            // SystemSpecsViewModel's RAM health rollup (read independently there too, same reasoning).
            MemoryRelatedBugcheckCount = CountMemoryRelatedBugchecks(events),
            // #464: boot-start/system-start driver load failures - also read independently by the
            // Devices & Drivers tab (ReadBootDriverLoadFailures is public for that reason).
            BootDriverLoadFailures = ReadBootDriverLoadFailures(),
            // #487/#488/#489/#490/#492: the broad WHEA-Logger read (every event ID, not just #447's
            // event 47), its daily corrected-error trend, and its correlation against the crash-like
            // events already gathered above.
            WheaHardwareErrors = wheaEvents,
            DailyWheaCorrectedCounts = BuildDailyWheaCorrectedCounts(wheaEvents),
            HardwareErrorCorrelations = BuildHardwareErrorCorrelations(crashTimelineEvents, wheaEvents),
        };
    }

    // #451: the subset of BugcheckCodeLookup's known STOP codes that point specifically at RAM/
    // memory-management failures rather than a driver/software fault - MEMORY_MANAGEMENT,
    // PFN_LIST_CORRUPT, PAGE_FAULT_IN_NONPAGED_AREA, the two INPAGE_ERROR codes (disk-adjacent but
    // triggered by a failed page-in, which a bad DIMM can cause), and WHEA_UNCORRECTABLE_ERROR
    // (the uncorrected counterpart to the corrected-error events read above).
    private static readonly HashSet<uint> MemoryRelatedBugcheckCodes = new()
    {
        0x0000001A, 0x0000004E, 0x00000050, 0x00000077, 0x0000007A, 0x00000124,
    };

    private static int CountMemoryRelatedBugchecks(List<StabilityEvent> events)
    {
        int count = 0;
        foreach (var e in events)
        {
            if (e.BugcheckCode is not { } code) continue;
            if (IsMemoryRelatedBugcheckHex(code)) count++;
        }
        return count;
    }

    private static bool IsMemoryRelatedBugcheckHex(string code)
    {
        string hex = code.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? code[2..] : code;
        return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint parsed)
            && MemoryRelatedBugcheckCodes.Contains(parsed);
    }

    /// <summary>#451: dedicated, lightweight count of Kernel-Power 41 events whose extracted
    /// bugcheck code falls in MemoryRelatedBugcheckCodes - reads just the System log's own
    /// Kernel-Power 41 entries (not the full dual-log Query() scan), so SystemSpecsService's RAM
    /// health rollup can call this independently of the Stability tab, the same "public, targeted,
    /// no ViewModel coupling" shape as ReadCorrectedMemoryErrors/ReadMemoryDiagnosticResult above.</summary>
    public int ReadMemoryRelatedBugcheckCount()
    {
        int count = 0;
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[(EventID={KernelPowerEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int scanned = 0;
            const int maxEvents = 200;
            while (scanned < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    scanned++;
                    var code = ExtractBugcheckCode(record);
                    if (code is not null && IsMemoryRelatedBugcheckHex(code)) count++;
                }
            }
        }
        catch
        {
            // Log unavailable/access denied - degrade to 0, same as every other targeted query here.
        }
        return count;
    }

    // #447: Microsoft-Windows-WHEA-Logger event ID 47 - "A corrected hardware error has occurred",
    // Windows' own corrected-ECC-memory-error log entry (Reliability Monitor reads the same
    // provider/event for its own memory-error reporting). DIMM/physical-address hints are a
    // best-effort regex over the event's own formatted message text - WHEA's message layout isn't
    // a documented, versioned contract (the same caveat ExtractBugcheckCode already carries for a
    // different event), so both stay null when the message doesn't match a recognized shape.
    private const string WheaLoggerProvider = "Microsoft-Windows-WHEA-Logger";
    private const int CorrectedMemoryErrorEventId = 47;
    private const int MaxCorrectedMemoryErrorsReturned = 100;

    private static readonly Regex PhysicalAddressRegex = new(@"Physical\s*Address:\s*(0x[0-9A-Fa-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DimmHintRegex = new(@"(DIMM\s*[0-9A-Za-z]+|Memory Module:\s*[^\r\n]+|Channel:\s*[0-9A-Za-z]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Public (not just Query()'s private use) so SystemSpecsService can read the same
    /// figure independently for the System Specs memory section (#447) without depending on
    /// StabilityViewModel - a single, cheap, dedicated targeted query (same shape as
    /// ReadLowMemoryEvents), not the full dual-log Query() scan.</summary>
    public List<CorrectedMemoryErrorEvent> ReadCorrectedMemoryErrors()
    {
        var results = new List<CorrectedMemoryErrorEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{WheaLoggerProvider}'] and (EventID={CorrectedMemoryErrorEventId}) and " +
                $"TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxCorrectedMemoryErrorsReturned && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is null) continue;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    var addressMatch = PhysicalAddressRegex.Match(message);
                    var dimmMatch = DimmHintRegex.Match(message);

                    results.Add(new CorrectedMemoryErrorEvent
                    {
                        TimeCreated = record.TimeCreated.Value,
                        PhysicalAddressHint = addressMatch.Success ? addressMatch.Groups[1].Value : null,
                        DimmHint = dimmMatch.Success ? dimmMatch.Groups[1].Value.Trim() : null,
                        RawMessage = Truncate(message, 400),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable, or no WHEA-capable hardware on this system at all - "none
            // found", same degrade-to-empty shape as every other targeted query in this service.
        }
        return results;
    }

    // #449: the built-in Windows Memory Diagnostic's own results provider/event IDs - documented
    // by Microsoft (Event Viewer's own "MemoryDiagnostics-Results" log/source). 1101 fires whether
    // or not errors were found; 1201 is used on some Windows versions for the same purpose - both
    // are queried together and the newest match wins.
    private const string MemoryDiagnosticsProvider = "Microsoft-Windows-MemoryDiagnostics-Results";
    private static readonly int[] MemoryDiagnosticsResultEventIds = { 1101, 1201 };

    /// <summary>Public for the same reason ReadCorrectedMemoryErrors is - SystemSpecsService reads
    /// this directly for the System Specs "last diagnostic run" card (#449), independent of the
    /// Stability tab. No lookback-window filter (unlike every other targeted query here) - the
    /// diagnostic runs rarely enough (a manual, boot-time action) that "the most recent one ever
    /// logged, whenever that was" is more useful than silently going blank once it's 31 days old.</summary>
    public MemoryDiagnosticResultInfo? ReadMemoryDiagnosticResult()
    {
        try
        {
            string idFilter = string.Join(" or ", MemoryDiagnosticsResultEventIds.Select(id => $"EventID={id}"));
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{MemoryDiagnosticsProvider}'] and ({idFilter})]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            if (reader.ReadEvent() is not { } record) return null;
            using (record)
            {
                if (record.TimeCreated is null) return null;

                string message;
                try { message = record.FormatDescription() ?? string.Empty; }
                catch { message = string.Empty; }

                // The rendered message states the outcome in plain English ("no memory problems
                // ... " vs. "... detected ... errors ..."); this is the documented, stable wording
                // Microsoft uses across Windows versions for this specific event, but is still
                // treated as best-effort text matching rather than a structured field - an
                // unrecognized wording degrades to null (shown as "couldn't determine the result"),
                // never a guessed pass/fail.
                bool? passed = null;
                if (message.Length > 0)
                {
                    if (message.Contains("did not detect any errors", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("no memory problems", StringComparison.OrdinalIgnoreCase))
                        passed = true;
                    else if (message.Contains("detected", StringComparison.OrdinalIgnoreCase) &&
                             message.Contains("error", StringComparison.OrdinalIgnoreCase))
                        passed = false;
                }

                return new MemoryDiagnosticResultInfo
                {
                    TimeCreated = record.TimeCreated.Value,
                    Passed = passed,
                    StatusText = Truncate(message, 300),
                };
            }
        }
        catch
        {
            // Provider/log unavailable, or the diagnostic has genuinely never been run on this
            // machine - "never run", not an error.
            return null;
        }
    }

    // #439: the specific event ID (out of everything ReadLowMemoryEvents/ReadPoolExhaustionEvents
    // already read from this same provider) that carries the ranked top-consumer list.
    private const int OutOfMemoryDiagnosedEventId = 2004;

    // Matches the documented message template's repeated "Name (Pid) consumed N bytes" clauses,
    // e.g. "chrome.exe (5892) consumed 1073741824 bytes, ... and MsMpEng.exe (908) consumed
    // 192774144 bytes." A leading "and " before the last clause is stripped from the captured name
    // below rather than folded into the pattern, since it only ever appears once per message.
    private static readonly Regex OomConsumerRegex = new(
        @"([A-Za-z0-9_.\- ]+?)\s*\((\d+)\)\s*consumed\s*([\d,]+)\s*bytes",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<OutOfMemoryIncident> ReadOutOfMemoryIncidents()
    {
        var results = new List<OutOfMemoryIncident>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{ResourceExhaustionProvider}'] and (EventID={OutOfMemoryDiagnosedEventId}) and " +
                $"TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
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
                    if (record.TimeCreated is null) continue;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    var consumers = new List<OomTopConsumer>();
                    foreach (Match m in OomConsumerRegex.Matches(message))
                    {
                        string name = m.Groups[1].Value.Trim();
                        if (name.StartsWith("and ", StringComparison.OrdinalIgnoreCase)) name = name[4..].Trim();
                        if (name.Length == 0) continue;
                        if (!int.TryParse(m.Groups[2].Value, out int pid)) continue;
                        if (!long.TryParse(m.Groups[3].Value.Replace(",", ""), out long bytes)) continue;

                        consumers.Add(new OomTopConsumer { ProcessName = name, Pid = pid, Bytes = bytes });
                    }

                    results.Add(new OutOfMemoryIncident
                    {
                        TimeCreated = record.TimeCreated.Value,
                        TopConsumers = consumers,
                        RawMessage = Truncate(message, 500),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "none found", same as every other event-log
            // read in this service.
        }
        return results;
    }

    // #427: the classic pool-starvation event signature - Srv 2019 (nonpaged pool exhausted) and
    // 2020 (paged pool exhausted) are logged at Warning/Error level by the SMB server component,
    // event 333 ("The registry cannot flush changes...") is the classic secondary symptom of the
    // same underlying exhaustion, and Resource-Exhaustion-Detector's own entries are queried
    // separately here (rather than reusing ReadLowMemoryEvents' count-only result above) since
    // this needs the actual event list to show, not just a count.
    private const int SrvNonpagedPoolExhaustedEventId = 2019;
    private const int SrvPagedPoolExhaustedEventId = 2020;
    private const int RegistryFlushFailedEventId = 333;

    private static List<PoolExhaustionEvent> ReadPoolExhaustionEvents()
    {
        var results = new List<PoolExhaustionEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[(EventID={SrvNonpagedPoolExhaustedEventId} or EventID={SrvPagedPoolExhaustedEventId} or " +
                $"EventID={RegistryFlushFailedEventId} or Provider[@Name='{ResourceExhaustionProvider}']) and " +
                $"TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
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
                    if (record.TimeCreated is null) continue;

                    results.Add(new PoolExhaustionEvent
                    {
                        TimeCreated = record.TimeCreated.Value,
                        ProviderName = record.ProviderName ?? string.Empty,
                        EventId = record.Id,
                        Explanation = ExplainPoolExhaustionEvent(record.Id, record.ProviderName ?? string.Empty),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "none found", same as every other event-log
            // read in this service.
        }
        return results;
    }

    private static string ExplainPoolExhaustionEvent(int eventId, string providerName) => eventId switch
    {
        SrvNonpagedPoolExhaustedEventId =>
            "The SMB server (Srv) reported nonpaged pool exhausted - a driver is very likely leaking nonpaged pool; a hard crash (bugcheck), not just a slowdown, becomes possible if this keeps happening.",
        SrvPagedPoolExhaustedEventId =>
            "The SMB server (Srv) reported paged pool exhausted - a driver is very likely leaking paged pool; file sharing/network I/O can start failing outright once this pool is exhausted.",
        RegistryFlushFailedEventId =>
            "The registry couldn't flush changes to disk - a classic secondary symptom of pool or disk-space exhaustion, not usually a registry problem in its own right.",
        _ when providerName.Equals(ResourceExhaustionProvider, StringComparison.OrdinalIgnoreCase) =>
            "Windows' own resource-exhaustion detector flagged available memory (physical RAM and/or commit) running critically low.",
        _ => "Matches the classic pool-starvation event signature.",
    };

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

    // #463 (event-log half): Microsoft-Windows-Kernel-PnP/Configuration is a separate, analytic-
    // style channel (not the plain System log) that logs the kernel PnP manager's own device
    // configuration attempts - 411/442 (explicitly called out by the suggestion) fall inside the
    // broader 400-series range this queries. This channel is disabled by default on a number of
    // Windows editions/builds (it needs `wevtutil sl ... /e:true` to turn on) - EventLogQuery/
    // EventLogReader throw when a channel is disabled or doesn't exist, so this degrades to "none
    // found" exactly like every other targeted query in this service rather than surfacing that as
    // an error.
    private const string PnpConfigurationLog = "Microsoft-Windows-Kernel-PnP/Configuration";

    public List<PnpConfigurationFailure> ReadPnpConfigurationFailures()
    {
        var results = new List<PnpConfigurationFailure>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery(PnpConfigurationLog, PathType.LogName,
                $"*[System[(EventID >= 400 and EventID <= 499) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
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
                    if (record.TimeCreated is null) continue;

                    string message;
                    try { message = record.FormatDescription() ?? $"Event {record.Id}"; }
                    catch { message = $"Event {record.Id}"; }

                    results.Add(new PnpConfigurationFailure
                    {
                        TimeCreated = record.TimeCreated.Value,
                        EventId = record.Id,
                        Level = record.LevelDisplayName ?? string.Empty,
                        Message = Truncate(message, 400),
                    });
                }
            }
        }
        catch
        {
            // Channel disabled/unavailable, or nothing logged - "none found", not an error.
        }
        return results;
    }

    // #464: boot-start/system-start driver load failures. Two independent sources, both queried
    // together since they cover different failure shapes: the Service Control Manager's own
    // 7000/7001 ("the X service failed to start"/"...depends on a service that failed") and 7026
    // ("the following boot-start or system-start driver(s) failed to load: ...", which can name
    // several drivers in one event), plus the kernel PnP manager's event 219 ("the driver
    // \Driver\X failed to load for the device Y").
    private const int ServiceFailedToStartEventId = 7000;
    private const int ServiceDependencyFailedEventId = 7001;
    private const int BootStartDriversFailedEventId = 7026;
    private const int PnpDriverFailedToLoadEventId = 219;

    private static readonly Regex ServiceFailedNameRegex = new(
        @"^The\s+(.+?)\s+service failed to start", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BootStartDriversNameRegex = new(
        @"failed to load:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex PnpDriverNameRegex = new(
        @"\\Driver\\([^\s""]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Public - read independently by both StabilityViewModel (via Query(), for the
    /// Stability tab's own small boot-driver card) and DevicesDriversViewModel (directly, for the
    /// Devices &amp; Drivers tab), the same dual-read shape ReadCorrectedMemoryErrors already uses
    /// rather than one tab depending on the other's ViewModel.</summary>
    public List<BootDriverLoadFailure> ReadBootDriverLoadFailures()
    {
        var results = new List<BootDriverLoadFailure>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[((Provider[@Name='Service Control Manager'] and " +
                $"(EventID={ServiceFailedToStartEventId} or EventID={ServiceDependencyFailedEventId} or EventID={BootStartDriversFailedEventId})) " +
                $"or (EventID={PnpDriverFailedToLoadEventId})) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
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
                    if (record.TimeCreated is null) continue;

                    string message;
                    try { message = record.FormatDescription() ?? $"Event {record.Id}"; }
                    catch { message = $"Event {record.Id}"; }

                    results.Add(new BootDriverLoadFailure
                    {
                        TimeCreated = record.TimeCreated.Value,
                        EventId = record.Id,
                        ProviderName = record.ProviderName ?? string.Empty,
                        DriverName = ExtractBootDriverName(record.Id, message),
                        Message = Truncate(message, 400),
                    });
                }
            }
        }
        catch
        {
            // Log unavailable/access denied, or none found - degrade to "nothing found".
        }
        return results;
    }

    /// <summary>Best-effort driver/service name extraction from the event's own formatted message -
    /// not a documented, versioned contract for any of these three event IDs, so an unmatched
    /// message just leaves DriverName null (shown as "Unknown") rather than a guess.</summary>
    private static string? ExtractBootDriverName(int eventId, string message)
    {
        Match match = eventId switch
        {
            ServiceFailedToStartEventId or ServiceDependencyFailedEventId => ServiceFailedNameRegex.Match(message),
            BootStartDriversFailedEventId => BootStartDriversNameRegex.Match(message),
            PnpDriverFailedToLoadEventId => PnpDriverNameRegex.Match(message),
            _ => Match.Empty,
        };
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // ------------------------------------------------------------------------------------------
    // #487/#488/#489/#490/#492: the broad Microsoft-Windows-WHEA-Logger read - every event ID from
    // the provider (not just #447's ReadCorrectedMemoryErrors, which stays as its own narrower,
    // message-text-based event-47 slice), decoded via CperDecoder's CPER binary parse of the
    // event's own "RawData" field where possible. #447's event 47 records show up in this broad
    // list too (as WheaErrorSourceType.PlatformMemory, Severity.Corrected) - cross-checked against
    // the existing message-text reading rather than conflicting with it.
    // ------------------------------------------------------------------------------------------

    private const int MaxWheaEventsReturned = 300;

    /// <summary>#492: how far back before a crash/TDR/unexpected-shutdown event a WHEA hardware
    /// error record still counts as "shortly before it" - five minutes is generous enough to catch
    /// the common case (a hardware fault triggering an immediate bugcheck/reset) without stretching
    /// so wide that unrelated errors from earlier in the session get pulled in. Stated explicitly in
    /// the UI text this backs, since the exact width is a judgment call, not a documented constant.</summary>
    private static readonly TimeSpan HardwareErrorCorrelationWindow = TimeSpan.FromMinutes(5);

    private static readonly Regex WheaRawDataHexRegex = new(
        @"<Data Name=""RawData"">([0-9A-Fa-f]+)</Data>", RegexOptions.Compiled);

    /// <summary>#489: bus/device/function -&gt; friendly name, built once per Query() call from
    /// currently-present PCI devices (PnpDeviceTreeService.BuildPciLocationLookup) rather than once
    /// per WHEA event - a single native enumeration pass is plenty cheap enough to repeat per
    /// refresh, and this keeps the lookup fresh against whatever's plugged in right now.</summary>
    public List<WheaHardwareErrorEvent> ReadWheaHardwareErrors()
    {
        var results = new List<WheaHardwareErrorEvent>();
        Dictionary<(int, int, int), string>? pciLocations = null;

        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{WheaLoggerProvider}'] and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxWheaEventsReturned && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is null) continue;

                    string message;
                    try { message = record.FormatDescription() ?? $"WHEA-Logger event {record.Id}"; }
                    catch { message = $"WHEA-Logger event {record.Id}"; }

                    byte[]? raw = ExtractWheaRawData(record);
                    var decoded = raw is not null ? CperDecoder.Decode(raw) : null;

                    if (decoded is not null)
                    {
                        pciLocations ??= SafeBuildPciLocationLookup();
                        results.Add(BuildWheaEventFromDecoded(record.TimeCreated.Value, record.Id, decoded, message, pciLocations));
                    }
                    else
                    {
                        results.Add(BuildWheaEventFallback(record, message));
                    }
                }
            }
        }
        catch
        {
            // Provider/log unavailable, or no WHEA-capable hardware on this system - degrade to
            // "none found", same as every other targeted query in this service.
        }
        return results;
    }

    private static Dictionary<(int, int, int), string> SafeBuildPciLocationLookup()
    {
        try { return PnpDeviceTreeService.BuildPciLocationLookup(); }
        catch { return new Dictionary<(int, int, int), string>(); }
    }

    /// <summary>Retrieves the event's raw CPER binary payload - first from a byte[]-typed property
    /// (how a manifest-based provider's binary EventData field normally surfaces through
    /// EventRecord.Properties), falling back to a regex pull of the "RawData" element out of the
    /// event's own rendered XML when that doesn't turn up anything. Either gap (no byte[] property,
    /// no matching XML element) just means this specific event falls back to BuildWheaEventFallback
    /// below - never a thrown exception out of this method.</summary>
    private static byte[]? ExtractWheaRawData(EventRecord record)
    {
        try
        {
            foreach (var prop in record.Properties)
            {
                if (prop.Value is byte[] { Length: > 16 } bytes) return bytes;
            }

            string xml = record.ToXml();
            var match = WheaRawDataHexRegex.Match(xml);
            if (match.Success && match.Groups[1].Value.Length % 2 == 0)
                return Convert.FromHexString(match.Groups[1].Value);
        }
        catch
        {
            // Malformed/unreadable event data - degrade to null; the caller's message-text fallback
            // still covers this event.
        }
        return null;
    }

    private static WheaHardwareErrorEvent BuildWheaEventFromDecoded(
        DateTime time, int eventId, CperRecord rec, string message, Dictionary<(int, int, int), string> pciLocations)
    {
        var pcieSection = rec.Sections.FirstOrDefault(s => s.Pcie is not null);
        var procIaSection = rec.Sections.FirstOrDefault(s => s.ProcessorIa is not null);

        return new WheaHardwareErrorEvent
        {
            TimeCreated = time,
            EventId = eventId,
            SourceType = rec.SourceType,
            Severity = rec.Severity,
            Component = DescribeWheaComponent(rec.SourceType),
            RawMessage = Truncate(message, 400),
            StructuredDecodeSucceeded = true,
            Pcie = pcieSection?.Pcie is { } p ? BuildPcieDetail(p, pciLocations) : null,
            MachineCheck = procIaSection?.ProcessorIa is { } ia ? BuildMachineCheckDetail(ia) : null,
        };
    }

    /// <summary>The record's binary payload couldn't be retrieved or didn't parse - falls back to a
    /// Level-derived severity estimate (Windows' own Critical/Error/Warning/Informational levels
    /// map reasonably onto Fatal/Recoverable/Corrected/Informational, WHEA-Logger's own severities)
    /// rather than leaving the row entirely blank; StructuredDecodeSucceeded=false marks this as an
    /// estimate, not a value read off the record itself.</summary>
    private static WheaHardwareErrorEvent BuildWheaEventFallback(EventRecord record, string message)
    {
        var severity = record.Level switch
        {
            1 => WheaErrorSeverity.Fatal,
            2 => WheaErrorSeverity.Recoverable,
            3 => WheaErrorSeverity.Corrected,
            4 => WheaErrorSeverity.Informational,
            _ => WheaErrorSeverity.Unknown,
        };
        return new WheaHardwareErrorEvent
        {
            TimeCreated = record.TimeCreated ?? DateTime.MinValue,
            EventId = record.Id,
            SourceType = WheaErrorSourceType.Unknown,
            Severity = severity,
            Component = "Unknown (binary error record unavailable)",
            RawMessage = Truncate(message, 400),
            StructuredDecodeSucceeded = false,
        };
    }

    private static string DescribeWheaComponent(WheaErrorSourceType type) => type switch
    {
        WheaErrorSourceType.MachineCheck => "Processor (machine check)",
        WheaErrorSourceType.PciExpress => "PCI Express",
        WheaErrorSourceType.PlatformMemory => "Memory",
        WheaErrorSourceType.Nmi => "Non-maskable interrupt (NMI)",
        WheaErrorSourceType.Other => "Other hardware error source",
        _ => "Unknown",
    };

    // #489: PCI Express AER Correctable/Uncorrectable Error Status register bit assignments - the
    // PCI Express Base Specification's own documented, stable bit numbers (Advanced Error Reporting
    // Extended Capability), not a Windows- or vendor-specific convention.
    private static void AppendUncorrectableAerFlags(uint status, List<string> flags)
    {
        if ((status & (1u << 4)) != 0) flags.Add("Data Link Protocol Error");
        if ((status & (1u << 5)) != 0) flags.Add("Surprise Down Error");
        if ((status & (1u << 12)) != 0) flags.Add("Poisoned TLP Received");
        if ((status & (1u << 13)) != 0) flags.Add("Flow Control Protocol Error");
        if ((status & (1u << 14)) != 0) flags.Add("Completion Timeout");
        if ((status & (1u << 15)) != 0) flags.Add("Completer Abort");
        if ((status & (1u << 16)) != 0) flags.Add("Unexpected Completion");
        if ((status & (1u << 17)) != 0) flags.Add("Receiver Overflow");
        if ((status & (1u << 18)) != 0) flags.Add("Malformed TLP");
        if ((status & (1u << 19)) != 0) flags.Add("ECRC Error");
        if ((status & (1u << 20)) != 0) flags.Add("Unsupported Request Error");
    }

    private static void AppendCorrectableAerFlags(uint status, List<string> flags)
    {
        if ((status & (1u << 0)) != 0) flags.Add("Receiver Error");
        if ((status & (1u << 6)) != 0) flags.Add("Bad TLP");
        if ((status & (1u << 7)) != 0) flags.Add("Bad DLLP");
        if ((status & (1u << 8)) != 0) flags.Add("REPLAY_NUM Rollover");
        if ((status & (1u << 12)) != 0) flags.Add("Replay Timer Timeout");
        if ((status & (1u << 13)) != 0) flags.Add("Advisory Non-Fatal Error");
        if ((status & (1u << 14)) != 0) flags.Add("Corrected Internal Error");
        if ((status & (1u << 15)) != 0) flags.Add("Header Log Overflow");
    }

    private static PcieAerDetail BuildPcieDetail(CperPcie p, Dictionary<(int, int, int), string> pciLocations)
    {
        var flags = new List<string>();
        bool isUncorrectable = false;
        if (p.UncorrectableStatus is { } u && u != 0)
        {
            isUncorrectable = true;
            AppendUncorrectableAerFlags(u, flags);
        }
        if (p.CorrectableStatus is { } c && c != 0)
            AppendCorrectableAerFlags(c, flags);

        pciLocations.TryGetValue((p.Bus, p.Device, p.Function), out var friendlyName);

        return new PcieAerDetail
        {
            Segment = p.Segment,
            Bus = p.Bus,
            Device = p.Device,
            Function = p.Function,
            VendorId = p.VendorId,
            DeviceId = p.DeviceId,
            IsUncorrectable = isUncorrectable,
            StatusFlags = flags,
            FriendlyDeviceName = friendlyName,
        };
    }

    /// <summary>Picks the most notable decoded bank (an uncorrected one if any, else the first) as
    /// this event's single machine-check detail - a WHEA-Logger record conventionally carries one
    /// bank per event in the overwhelming majority of real-world cases, and this keeps the display
    /// to one coherent "what the hardware reported" panel rather than a nested sub-list.</summary>
    private static MachineCheckDetail BuildMachineCheckDetail(CperProcessorIa ia)
    {
        var bank = ia.Banks.FirstOrDefault(b => b.Uncorrected == true) ?? ia.Banks.FirstOrDefault();
        return new MachineCheckDetail
        {
            Bank = bank?.BankNumber,
            RawMciStatus = bank?.RawMciStatus,
            ApicId = ia.LocalApicId,
            Uncorrected = bank?.Uncorrected,
            ProcessorContextCorrupt = bank?.ProcessorContextCorrupt,
            Overflow = bank?.Overflow,
        };
    }

    /// <summary>#488: corrected-severity WHEA records per day across the lookback window, oldest
    /// first - the same zero-filled daily-bucket shape as BuildDailyCounts above, just over
    /// WheaHardwareErrors' own Severity field instead of System/Application Critical/Error entries.</summary>
    private static List<DailyEventCount> BuildDailyWheaCorrectedCounts(List<WheaHardwareErrorEvent> wheaEvents)
    {
        var counts = wheaEvents
            .Where(e => e.Severity == WheaErrorSeverity.Corrected)
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

    /// <summary>#492: for each crash/TDR/unexpected-shutdown event, finds the nearest WHEA hardware-
    /// error record within HardwareErrorCorrelationWindow beforehand, if any - a pure re-correlation
    /// of two lists this method's caller already read, no new query. Framed throughout as a
    /// correlation, never a claimed cause - see HardwareErrorCorrelation's remarks.</summary>
    private static List<HardwareErrorCorrelation> BuildHardwareErrorCorrelations(
        List<StabilityEvent> crashLikeEvents, List<WheaHardwareErrorEvent> wheaEvents)
    {
        var results = new List<HardwareErrorCorrelation>();
        if (crashLikeEvents.Count == 0 || wheaEvents.Count == 0) return results;

        var orderedWhea = wheaEvents.OrderBy(w => w.TimeCreated).ToList();
        foreach (var crash in crashLikeEvents)
        {
            var windowStart = crash.TimeCreated - HardwareErrorCorrelationWindow;
            var inWindow = orderedWhea
                .Where(w => w.TimeCreated <= crash.TimeCreated && w.TimeCreated >= windowStart)
                .ToList();
            if (inWindow.Count == 0) continue;

            var nearest = inWindow.OrderByDescending(w => w.TimeCreated).First();
            results.Add(new HardwareErrorCorrelation
            {
                CrashTime = crash.TimeCreated,
                CrashDescription = DescribeCrashLikeEvent(crash),
                HardwareErrorTime = nearest.TimeCreated,
                HardwareErrorDescription = $"{DescribeWheaComponent(nearest.SourceType)} - {nearest.Severity}",
                Gap = crash.TimeCreated - nearest.TimeCreated,
                HardwareErrorsInWindow = inWindow.Count,
            });
        }
        return results.OrderByDescending(c => c.CrashTime).ToList();
    }

    private static string DescribeCrashLikeEvent(StabilityEvent e) => e.EventId switch
    {
        KernelPowerEventId => "Unexpected shutdown (Kernel-Power 41)",
        LegacyUncleanShutdownEventId => "Unexpected shutdown (EventLog 6008)",
        BlueScreenEventId => "Blue screen report (Windows Error Reporting 1001)",
        TdrEventId => "GPU driver timeout/reset (TDR, event 4101)",
        _ => $"Event {e.EventId}",
    };
}
