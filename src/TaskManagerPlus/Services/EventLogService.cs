using System.Diagnostics.Eventing.Reader;
using System.Globalization;
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
            ThermalCriticalEvents = ReadThermalCriticalEvents(),
            LastUnexpectedShutdownBugcheckCode = mostRecentShutdownEvent?.BugcheckCode,
        };
    }

    // #602: Kernel-Processor-Power event 37 ("the speed of processor N is being limited by system
    // firmware... for X seconds") and its 38 recovery counterpart - the one authoritative,
    // non-heuristic statement Windows itself makes about firmware-side CPU throttling.
    private const string ProcessorPowerProvider = "Microsoft-Windows-Kernel-Processor-Power";
    private const int FirmwareThrottleEventId = 37;
    private const int FirmwareThrottleRecoveryEventId = 38;

    public List<FirmwareThrottleEvent> ReadFirmwareThrottleEvents()
    {
        var result = new List<FirmwareThrottleEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{ProcessorPowerProvider}'] and (EventID={FirmwareThrottleEventId} or EventID={FirmwareThrottleRecoveryEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
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
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    result.Add(new FirmwareThrottleEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        IsRecovery = record.Id == FirmwareThrottleRecoveryEventId,
                        Message = Truncate(message, 240),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable (older Windows builds don't log this provider at all) -
            // degrade to "none found".
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    // #606: the "a thermal zone exceeded its critical/passive trip point" family, plus ACPI
    // thermal-shutdown records - matched by provider + a message keyword rather than a hardcoded
    // event ID, since IDs for this family vary by Windows build (unlike Kernel-Power 41, which is
    // stable across every build this app targets).
    private static readonly string[] ThermalCriticalProviders =
    {
        "Microsoft-Windows-Kernel-Power",
        "Microsoft-Windows-Kernel-Acpi",
        "ACPI",
    };

    private static readonly string[] ThermalCriticalKeywords =
    {
        "thermal zone", "critical temperature", "critical trip point", "thermal shutdown", "thermal event",
    };

    public List<StabilityEvent> ReadThermalCriticalEvents()
    {
        var result = new List<StabilityEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var providerFilter = string.Join(" or ", ThermalCriticalProviders.Select(p => $"Provider[@Name='{p}']"));
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[({providerFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 500; // Kernel-Power alone can log a lot of unrelated entries over 30 days
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { continue; } // can't keyword-match without the formatted message

                    if (!ThermalCriticalKeywords.Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    result.Add(new StabilityEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        LogName = "System",
                        ProviderName = record.ProviderName ?? string.Empty,
                        EventId = record.Id,
                        Level = record.LevelDisplayName ?? string.Empty,
                        Message = Truncate(message, 300),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable - degrade to "none found", same as every other event-log
            // read in this service.
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    // #636: WHEA-Logger (Windows Hardware Error Architecture) events - the app's first WHEA
    // surface. Event 1 is a fatal/uncorrected machine check; 17 is a corrected machine check;
    // 18/19/20 are corrected platform/PCIe errors; 47 is a corrected memory error. IDs are stable
    // across the Windows versions this app targets (unlike the thermal-critical family above,
    // which varies by build), so this is filtered by EventID the same way ReadFirmwareThrottleEvents
    // filters Kernel-Processor-Power 37/38.
    private const string WheaProvider = "Microsoft-Windows-WHEA-Logger";
    private static readonly int[] WheaEventIds = { 1, 17, 18, 19, 20, 47 };

    private static readonly Regex WheaErrorSourceRegex = new(@"Error Source:\s*([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WheaErrorTypeRegex = new(@"Error Type:\s*([^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WheaBankRegex = new(@"Bank\s*(?:Number)?:\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WheaBusDeviceFunctionRegex = new(
        @"Bus:Device:Function:\s*(?:0x)?([0-9A-Fa-f]+):(?:0x)?([0-9A-Fa-f]+):(?:0x)?([0-9A-Fa-f]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WheaSegmentRegex = new(@"Segment:\s*(?:0x)?([0-9A-Fa-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>#636: parses WHEA-Logger System-log events into a typed list - error source, bank
    /// (machine-check events), and PCIe segment/bus/device/function (platform/PCIe events) where
    /// the formatted message contains them. Every field not found in the message is left null/
    /// empty rather than guessed - WHEA-Logger's message layout is exactly as undocumented/
    /// unversioned as Kernel-Power 41's insertion strings (see ExtractBugcheckCode's remarks).</summary>
    public List<WheaEvent> ReadWheaEvents()
    {
        var result = new List<WheaEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var idFilter = string.Join(" or ", WheaEventIds.Select(id => $"EventID={id}"));
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{WheaProvider}'] and ({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
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
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    string errorSource = WheaErrorSourceRegex.Match(message) is { Success: true } srcMatch
                        ? srcMatch.Groups[1].Value.Trim() : string.Empty;
                    string errorType = WheaErrorTypeRegex.Match(message) is { Success: true } typeMatch
                        ? typeMatch.Groups[1].Value.Trim() : string.Empty;

                    int? bank = WheaBankRegex.Match(message) is { Success: true } bankMatch && int.TryParse(bankMatch.Groups[1].Value, out var b)
                        ? b : null;

                    int? segment = null, bus = null, device = null, function = null;
                    var bdf = WheaBusDeviceFunctionRegex.Match(message);
                    if (bdf.Success &&
                        int.TryParse(bdf.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var busVal) &&
                        int.TryParse(bdf.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var devVal) &&
                        int.TryParse(bdf.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var funcVal))
                    {
                        bus = busVal; device = devVal; function = funcVal;
                        var segMatch = WheaSegmentRegex.Match(message);
                        if (segMatch.Success && int.TryParse(segMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var segVal))
                            segment = segVal;
                    }

                    result.Add(new WheaEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        EventId = record.Id,
                        IsFatal = record.Id == 1,
                        CategoryText = DescribeWheaCategory(record.Id),
                        ErrorSourceText = errorSource,
                        Bank = bank,
                        BankHintText = MceBankHintLookup.Describe(string.IsNullOrEmpty(errorType) ? errorSource : errorType),
                        PcieSegment = segment,
                        PcieBus = bus,
                        PcieDevice = device,
                        PcieFunction = function,
                        Message = Truncate(message, 400),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable (no WHEA-capable hardware ever logged anything, or the
            // provider isn't registered on this Windows build) - degrade to "none found".
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
    }

    private static string DescribeWheaCategory(int eventId) => eventId switch
    {
        1 => "Fatal machine check",
        17 => "Corrected machine check",
        18 => "Corrected platform error",
        19 => "Corrected PCIe error",
        20 => "Corrected platform error",
        47 => "Corrected memory error",
        _ => "WHEA event",
    };

    // #626: Kernel-Power event 105 - AC/DC power-source transitions. A stable, well-known event ID
    // commonly used for exactly this purpose (e.g. Task Scheduler's built-in "on AC/DC power
    // change" triggers), unlike the thermal-critical family above.
    private const string KernelPowerProvider = "Microsoft-Windows-Kernel-Power";
    private const int PowerSourceChangeEventId = 105;

    /// <summary>#626: reads Kernel-Power 105 (AC/DC power-source change) events - the raw list;
    /// EnergyThermalsViewModel derives the "N times in the last hour" flapping count from these
    /// timestamps.</summary>
    public List<PowerSourceChangeEvent> ReadPowerSourceChangeEvents()
    {
        var result = new List<PowerSourceChangeEvent>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='{KernelPowerProvider}'] and (EventID={PowerSourceChangeEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
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
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    result.Add(new PowerSourceChangeEvent
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        Message = Truncate(message, 200),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable, or (the common desktop case) this event simply never
            // fires because there's no battery/AC adapter to transition between - degrade to
            // "none found".
        }
        return result.OrderByDescending(e => e.TimeCreated).ToList();
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
}
