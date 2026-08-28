using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #285/#286: Windows Defender scan state/schedule and real-time-scan hot-path reporting -
/// MSFT_MpComputerStatus/MSFT_MpPreference from the root\Microsoft\Windows\Defender WMI namespace
/// (the same class/namespace `Get-MpComputerStatus`/`Get-MpPreference` wrap), plus the
/// Microsoft-Windows-Windows Defender/Operational event log for #286's hot-path scan. This
/// namespace is entirely absent on a machine running third-party AV with Defender's engine fully
/// disabled - every read here degrades to IsAvailable=false rather than throwing or guessing, the
/// same "expected, correct outcome, not a bug" tier CLAUDE.md documents for SensorMonitorService.
/// </summary>
public static class DefenderActivityService
{
    private const string DefenderNamespace = @"root\Microsoft\Windows\Defender";
    private const string OperationalLog = "Microsoft-Windows-Windows Defender/Operational";

    // Real-time scan start/finish, and threat-detection events - see the class remarks for why
    // this is a best-effort text-mined report, not a structured API.
    private static readonly int[] ScanEventIds = { 1000, 1001 };
    private static readonly int[] DetectionEventIds = { 1116, 1117 };

    // Best-effort file/directory path extraction from a formatted event message - the same
    // "regex the stable, user-facing sentence" tradeoff EventLogService's own FaultingModuleRegex
    // already takes, since these events carry no documented, versioned Path insertion-string index.
    private static readonly Regex FilePathRegex = new(@"[A-Za-z]:\\[^""\r\n]+", RegexOptions.Compiled);

    /// <summary>#285: "fine live" per the task framing - reads Defender's own WMI status/preference
    /// classes and builds a scan-activity sentence from them, riding ResponsivenessViewModel's own
    /// moderate background-activity cadence (not the fastest 2s tick, since a WMI query is heavier
    /// than a plain registry/perf-counter read) rather than being gated behind a manual button.</summary>
    public static async Task<DefenderScanInfo> ReadStatusAsync(double msMpEngCpuPercent) =>
        await Task.Run(() => ReadStatus(msMpEngCpuPercent));

    private static DefenderScanInfo ReadStatus(double msMpEngCpuPercent)
    {
        try
        {
            using var statusSearcher = new ManagementObjectSearcher(DefenderNamespace,
                "SELECT RealTimeProtectionEnabled, QuickScanStartTime, FullScanStartTime, AntivirusSignatureLastUpdated FROM MSFT_MpComputerStatus");
            using var statusResults = statusSearcher.Get();
            var status = statusResults.Cast<ManagementBaseObject>().FirstOrDefault();
            if (status is null)
            {
                return new DefenderScanInfo { IsAvailable = false, StatusText = "MSFT_MpComputerStatus returned no instance - Defender's engine may be disabled (a third-party AV may own real-time protection)." };
            }

            bool rtpEnabled = status["RealTimeProtectionEnabled"] is bool b && b;
            DateTime? quickStart = ToDateTimeOrNull(status["QuickScanStartTime"]);
            DateTime? fullStart = ToDateTimeOrNull(status["FullScanStartTime"]);
            DateTime? sigUpdated = ToDateTimeOrNull(status["AntivirusSignatureLastUpdated"]);

            int? cpuCap = null;
            bool idleOnly = false;
            string? scheduleTimeText = null;
            try
            {
                using var prefSearcher = new ManagementObjectSearcher(DefenderNamespace,
                    "SELECT ScanAvgCPULoadFactor, ScanOnlyIfIdleEnabled, ScanScheduleTime FROM MSFT_MpPreference");
                using var prefResults = prefSearcher.Get();
                var pref = prefResults.Cast<ManagementBaseObject>().FirstOrDefault();
                if (pref is not null)
                {
                    cpuCap = pref["ScanAvgCPULoadFactor"] is { } c ? Convert.ToInt32(c) : null;
                    idleOnly = pref["ScanOnlyIfIdleEnabled"] is bool ib && ib;
                    scheduleTimeText = pref["ScanScheduleTime"]?.ToString();
                }
            }
            catch
            {
                // MSFT_MpPreference unavailable/denied - status fields above still stand.
            }

            // #285: "quick flag, not a verdict" - Windows exposes no documented "a scan is running
            // right now" boolean, so this pairs MsMpEng's own CPU% (already polled by the Processes
            // tab) with whichever scan start-time is most recent, the same heuristic
            // SummaryViewModel's existing health-check rule already uses for the bare CPU number.
            DateTime? mostRecentStart = quickStart is { } q && fullStart is { } f
                ? (q > f ? q : f)
                : quickStart ?? fullStart;
            bool likelyActive = msMpEngCpuPercent >= 20 && mostRecentStart is { } started && (DateTime.Now - started) < TimeSpan.FromHours(6);
            string scanKind = mostRecentStart == fullStart && fullStart is not null ? "full" : "quick";

            string activityText = likelyActive && mostRecentStart is { } ms
                ? $"A scheduled {scanKind} scan started {FormatAgo(DateTime.Now - ms)} ago, and MsMpEng is at {msMpEngCpuPercent:0}% CPU" + (cpuCap is { } cap ? $" (CPU cap: {cap}%)." : ".")
                : msMpEngCpuPercent >= 20
                    ? $"MsMpEng is at {msMpEngCpuPercent:0}% CPU, but no recent scan start time was found - may be an on-access scan burst rather than a scheduled scan."
                    : "No active scan detected right now.";

            return new DefenderScanInfo
            {
                IsAvailable = true,
                StatusText = "Defender WMI status read OK.",
                RealTimeProtectionEnabled = rtpEnabled,
                QuickScanStartTime = quickStart,
                FullScanStartTime = fullStart,
                SignatureLastUpdated = sigUpdated,
                ScanAvgCpuLoadFactor = cpuCap,
                ScanOnlyIfIdleEnabled = idleOnly,
                ScanScheduleTimeText = scheduleTimeText,
                IsScanLikelyActive = likelyActive,
                ScanActivityText = activityText,
            };
        }
        catch (Exception ex)
        {
            return new DefenderScanInfo
            {
                IsAvailable = false,
                StatusText = $"Defender WMI namespace unavailable ({ex.Message}) - a third-party AV may own real-time protection with Defender's engine disabled.",
            };
        }
    }

    /// <summary>#286: on-demand real-time-scan hot-path report - a directory-level aggregation of
    /// scan/detection event messages, joined against the currently-configured exclusion list.
    /// Advisory only; this app makes no automatic changes to exclusions.</summary>
    public static async Task<DefenderHotPathResult> ReadHotPathsAsync(TimeSpan window) => await Task.Run(() =>
    {
        List<string> exclusions;
        try
        {
            using var searcher = new ManagementObjectSearcher(DefenderNamespace, "SELECT ExclusionPath FROM MSFT_MpPreference");
            using var results = searcher.Get();
            var pref = results.Cast<ManagementBaseObject>().FirstOrDefault();
            exclusions = pref?["ExclusionPath"] is string[] paths ? paths.ToList() : new List<string>();
        }
        catch
        {
            exclusions = new List<string>();
        }

        var byDir = new Dictionary<string, (int Count, DateTime Last)>(StringComparer.OrdinalIgnoreCase);
        bool logAvailable = true;
        try
        {
            long maxAgeMs = (long)window.TotalMilliseconds;
            var allIds = ScanEventIds.Concat(DetectionEventIds);
            string idFilter = string.Join(" or ", allIds.Select(id => $"EventID={id}"));
            var query = new EventLogQuery(OperationalLog, PathType.LogName,
                $"*[System[({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]");
            using var reader = new EventLogReader(query);
            int count = 0;
            const int maxEvents = 3000;
            while (count < maxEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    var match = FilePathRegex.Match(message);
                    if (!match.Success) continue;

                    string filePath = match.Value.Trim();
                    string dir;
                    try { dir = Path.GetDirectoryName(filePath) ?? filePath; }
                    catch { dir = filePath; }

                    var time = record.TimeCreated ?? DateTime.MinValue;
                    if (byDir.TryGetValue(dir, out var existing))
                        byDir[dir] = (existing.Count + 1, time > existing.Last ? time : existing.Last);
                    else
                        byDir[dir] = (1, time);
                }
            }
        }
        catch
        {
            logAvailable = false;
        }

        var rows = byDir
            .Select(kv => new DefenderHotPathRow
            {
                Directory = kv.Key,
                EventCount = kv.Value.Count,
                LastSeen = kv.Value.Last,
                IsAlreadyExcluded = exclusions.Any(e => kv.Key.StartsWith(e.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)),
            })
            .OrderByDescending(r => r.EventCount)
            .Take(50)
            .ToList();

        return new DefenderHotPathResult
        {
            IsAvailable = logAvailable,
            StatusText = !logAvailable
                ? $"{OperationalLog} isn't available on this system (or access was denied)."
                : rows.Count == 0
                    ? "No file paths could be mined from recent scan/detection events (or there were none in the lookback window)."
                    : $"{rows.Count} director{(rows.Count == 1 ? "y" : "ies")} seen in recent scan/detection event messages. Adding exclusions reduces protection - this is a read-only report, this app makes no automatic changes to Defender exclusions.",
            HotPaths = rows,
            ExclusionPaths = exclusions,
        };
    });

    private static DateTime? ToDateTimeOrNull(object? value)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return null;
        try
        {
            var dt = ManagementDateTimeConverter.ToDateTime(s);
            return dt == DateTime.MinValue ? null : dt;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatAgo(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{span.TotalHours:0.#}h" :
        span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0} min" :
        $"{span.TotalSeconds:0}s";
}
