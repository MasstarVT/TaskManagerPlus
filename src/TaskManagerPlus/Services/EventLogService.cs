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
        };
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
}
