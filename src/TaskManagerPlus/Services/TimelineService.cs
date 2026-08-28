using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #938-944 (Timeline panel, suggestions.md items 938-949): aggregates every lane's events from
/// this app's existing data sources - never a new poller, never fabricated data (CLAUDE.md's
/// "degrade to Unknown/0/hidden - never fabricate" applies here too: a source that's unavailable
/// simply contributes no events to its lane rather than a guessed placeholder). Every public
/// method here mirrors TroubleshootService's own "never throw past this class" convention - a
/// failed WMI/registry/file read degrades to an empty list.
///
/// Reuses TroubleshootService's already-written event-log-read (<see cref="TroubleshootService.ReadProviderEvents"/>),
/// shell-out (<see cref="TroubleshootService.RunCapturedAsync"/>), and pnputil-output-parsing
/// (<see cref="TroubleshootService.ParsePnpUtilDrivers"/>) helpers rather than duplicating them -
/// all three were bumped from private to internal specifically for this reuse (see #939's task
/// instructions).
/// </summary>
public static class TimelineService
{
    // Generous cap on how far back a WMI/event-log read reaches - #948's date-range preset (up to
    // "All") narrows further at render time, but a query still needs *some* bound so a decade-old
    // machine's Win32_ReliabilityRecords table doesn't get pulled in its entirety.
    private const int DefaultLookbackDays = 180;

    #region 941 - Crashes lane (Win32_ReliabilityRecords)

    // Win32_ReliabilityRecords' own SourceName values aren't a documented, versioned enum - this
    // is the same "recognizable text shape, not a guaranteed contract" caveat
    // TroubleshootService.CheckReliabilityRecords already carries for the same WMI class. Records
    // whose SourceName doesn't match one of these hints (e.g. "Application Install"/"Windows
    // Update", which Win32_ReliabilityRecords also logs) are left out of the Crashes lane
    // entirely, rather than guessed at.
    private static readonly string[] CrashSourceHints =
    {
        "Application Error", "Application Hang", "Application Fault", "Windows Error Reporting",
        "Blue Screen", "Bug Check", "System Failure", "Hardware Error", "Hardware Failure",
        "Kernel", "Disk", "Miscellaneous Failure",
    };

    /// <summary>#941: app crashes, hangs, and hardware failures from Win32_ReliabilityRecords -
    /// the same WMI class/query shape StabilityViewModel (via EventLogService's crash scan) and
    /// TroubleshootService.CheckReliabilityRecords already use, just without their narrower
    /// "within a week of one specific crash" filter, since this lane wants every record in the
    /// lookback window, not just ones near an already-known crash.</summary>
    public static List<TimelineEvent> GetReliabilityCrashEvents()
    {
        var events = new List<TimelineEvent>();
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-DefaultLookbackDays);
            using var searcher = new ManagementObjectSearcher(
                "SELECT SourceName, Message, TimeGenerated, ProductName FROM Win32_ReliabilityRecords");
            foreach (ManagementObject mo in searcher.Get())
            {
                DateTime? time = null;
                if (mo["TimeGenerated"] is string wmiTime)
                {
                    try { time = ManagementDateTimeConverter.ToDateTime(wmiTime); } catch { /* leave null */ }
                }
                if (time is null || time.Value < cutoff) continue;

                string source = (mo["SourceName"] as string ?? string.Empty).Trim();
                if (!CrashSourceHints.Any(h => source.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;

                string message = (mo["Message"] as string ?? string.Empty).Trim();
                string product = (mo["ProductName"] as string ?? string.Empty).Trim();

                events.Add(new TimelineEvent
                {
                    Lane = TimelineLane.Crashes,
                    Timestamp = time.Value,
                    Title = product.Length > 0 ? product : source,
                    Detail = message.Length > 0 ? message : source,
                    Source = "Win32_ReliabilityRecords",
                    IsFailure = true,
                });
            }
        }
        catch
        {
            // WMI class/namespace unavailable or access denied - degrade to "no crash records".
        }
        return events;
    }

    #endregion

    #region Service failures lane (Service Control Manager 7031/7034)

    /// <summary>Not one of the explicitly-numbered suggestions, but needed to make #944's
    /// "Crashes + Service-failures lanes" correlation scoring meaningful - Service Control
    /// Manager's own 7031 ("terminated unexpectedly") / 7034 ("terminated with the following
    /// error") events are the only *dated* record of a service failure Windows keeps by default;
    /// ServiceRow.HasFailedToStart (used elsewhere in this app) only reflects the current live
    /// state, with no timestamp of when it happened. Reuses TroubleshootService's generic
    /// event-log reader rather than a new EventLogQuery.</summary>
    public static List<TimelineEvent> GetServiceFailureEvents()
    {
        var raw = TroubleshootService.ReadProviderEvents("System", "Service Control Manager", new[] { 7031, 7034 }, DefaultLookbackDays, maxEvents: 200);
        return raw.Select(e => new TimelineEvent
        {
            Lane = TimelineLane.ServiceFailures,
            Timestamp = e.TimeCreated,
            Title = ExtractServiceNameFromMessage(e.Message) ?? "A service",
            Detail = TroubleshootService.Truncate(e.Message, 200),
            Source = $"System event {e.EventId} (Service Control Manager)",
            IsFailure = true,
        }).ToList();
    }

    private static string? ExtractServiceNameFromMessage(string message)
    {
        // "The <Name> service terminated unexpectedly..." - SCM's own 7031/7034 message text puts
        // the service's display name right after "The " on every Windows build this app targets.
        // Best-effort, same "not a documented/versioned layout" caveat every other message-text
        // scrape in this app carries (see TroubleshootService.CulpritPattern's remarks).
        var m = Regex.Match(message, @"^The (.+?) service");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    #endregion

    #region 940 - Windows Updates lane

    /// <summary>#940: Win32_QuickFixEngineering (install date + KB id, currently-installed
    /// hotfixes only) plus Microsoft-Windows-WindowsUpdateClient/Operational event 19 (install
    /// succeeded) / 20 (install failed) - the event log carries *failed* installs the QFE
    /// inventory never lists (a failed update was never actually applied, so it can't show up in
    /// "currently installed hotfixes"), which is exactly why both sources are read rather than
    /// just one. IsFailure differentiates the two visually per the suggestion text.</summary>
    public static List<TimelineEvent> GetWindowsUpdateEvents()
    {
        var events = new List<TimelineEvent>();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering");
            foreach (ManagementObject mo in searcher.Get())
            {
                string hotfix = (mo["HotFixID"] as string ?? string.Empty).Trim();
                if (hotfix.Length == 0) continue;
                string description = (mo["Description"] as string ?? string.Empty).Trim();
                string installedOnRaw = (mo["InstalledOn"] as string ?? string.Empty).Trim();
                if (!DateTime.TryParse(installedOnRaw, out var installedOn)) continue; // no usable date on this entry

                events.Add(new TimelineEvent
                {
                    Lane = TimelineLane.WindowsUpdates,
                    Timestamp = installedOn,
                    Title = hotfix,
                    Detail = description.Length > 0 ? description : "Installed successfully",
                    Source = "Win32_QuickFixEngineering",
                    IsFailure = false,
                });
            }
        }
        catch
        {
            // WMI class unavailable - degrade to whatever the event log below still finds.
        }

        var succeeded = TroubleshootService.ReadProviderEvents(
            "Microsoft-Windows-WindowsUpdateClient/Operational", null, new[] { 19 }, DefaultLookbackDays, maxEvents: 100);
        var failed = TroubleshootService.ReadProviderEvents(
            "Microsoft-Windows-WindowsUpdateClient/Operational", null, new[] { 20 }, DefaultLookbackDays, maxEvents: 100);

        events.AddRange(succeeded.Select(e => new TimelineEvent
        {
            Lane = TimelineLane.WindowsUpdates,
            Timestamp = e.TimeCreated,
            Title = ExtractUpdateTitle(e.Message) ?? "Windows Update installed",
            Detail = TroubleshootService.Truncate(e.Message, 200),
            Source = "Microsoft-Windows-WindowsUpdateClient/Operational (event 19)",
            IsFailure = false,
        }));
        events.AddRange(failed.Select(e => new TimelineEvent
        {
            Lane = TimelineLane.WindowsUpdates,
            Timestamp = e.TimeCreated,
            Title = "FAILED: " + (ExtractUpdateTitle(e.Message) ?? "Windows Update"),
            Detail = TroubleshootService.Truncate(e.Message, 200),
            Source = "Microsoft-Windows-WindowsUpdateClient/Operational (event 20)",
            IsFailure = true,
        }));

        return events;
    }

    private static string? ExtractUpdateTitle(string message)
    {
        var m = Regex.Match(message, @"""([^""]{4,140})""");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    #endregion

    #region 939 - Driver installs lane (setupapi.dev.log + pnputil /enum-drivers)

    private static readonly Regex SetupApiSectionStart = new(
        @">>>\s*\[(?<kind>Device Install \(.*?\))\s*-\s*(?<name>.+?)\]", RegexOptions.Compiled);
    private static readonly Regex SetupApiSectionTimestamp = new(
        @">>>\s*Section start (?<date>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\.\d{3})", RegexOptions.Compiled);

    /// <summary>
    /// #939: device-install sections from %SystemRoot%\inf\setupapi.dev.log (the primary source -
    /// it's the one place Windows records *when* a driver install was attempted, down to the
    /// millisecond), cross-checked against `pnputil /enum-drivers`' currently-staged package list
    /// for any package whose date wasn't already matched to a log section (log rotated away, or
    /// this Windows build's exact section wording didn't match the regex above). Reuses
    /// TroubleshootService's existing pnputil-output parser
    /// (<see cref="TroubleshootService.ParsePnpUtilDrivers"/>, originally written for #903's
    /// crash-correlation branch) rather than a second hand-rolled one, per this task's own
    /// instructions.
    /// </summary>
    public static async Task<List<TimelineEvent>> GetDriverInstallEventsAsync()
    {
        var events = new List<TimelineEvent>();

        try
        {
            string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "inf", "setupapi.dev.log");
            if (File.Exists(logPath))
            {
                // setupapi.dev.log stays open/appended-to by the OS - needs an explicit
                // FileShare.ReadWrite the plain File.ReadAllLines overload doesn't allow.
                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                string? pendingName = null;
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    var sectionMatch = SetupApiSectionStart.Match(line);
                    if (sectionMatch.Success)
                    {
                        pendingName = sectionMatch.Groups["name"].Value.Trim();
                        continue;
                    }
                    if (pendingName is null) continue;

                    var tsMatch = SetupApiSectionTimestamp.Match(line);
                    if (!tsMatch.Success) continue;

                    if (DateTime.TryParseExact(tsMatch.Groups["date"].Value, "yyyy/MM/dd HH:mm:ss.fff",
                            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var ts))
                    {
                        events.Add(new TimelineEvent
                        {
                            Lane = TimelineLane.DriverInstalls,
                            Timestamp = ts,
                            Title = pendingName,
                            Detail = "Device install section started",
                            Source = "setupapi.dev.log",
                            IsFailure = false,
                        });
                    }
                    pendingName = null;
                }
            }
        }
        catch
        {
            // Locked/inaccessible/missing (e.g. a fresh install, or a build that's rotated the log
            // away) - degrade to whatever pnputil below still finds.
        }

        try
        {
            var (output, exitCode) = await TroubleshootService.RunCapturedAsync("pnputil.exe", "/enum-drivers");
            if (exitCode == 0)
            {
                var staged = TroubleshootService.ParsePnpUtilDrivers(output);
                foreach (var d in staged.Where(d => d.Date is not null))
                {
                    bool alreadyCovered = events.Any(e =>
                        Math.Abs((e.Timestamp - d.Date!.Value).TotalDays) < 1 &&
                        (e.Title.Contains(d.Provider, StringComparison.OrdinalIgnoreCase) || d.Provider.Contains(e.Title, StringComparison.OrdinalIgnoreCase)));
                    if (alreadyCovered) continue;

                    events.Add(new TimelineEvent
                    {
                        Lane = TimelineLane.DriverInstalls,
                        Timestamp = d.Date!.Value,
                        Title = d.Provider,
                        Detail = $"Staged driver package: {d.PublishedName} (date-only - no time-of-day from pnputil)",
                        Source = "pnputil /enum-drivers",
                        IsFailure = false,
                    });
                }
            }
        }
        catch
        {
            // pnputil unavailable - the setupapi.dev.log events above (if any) still stand.
        }

        return events;
    }

    #endregion

    #region Software installs lane (registry Uninstall InstallDate)

    private static readonly string[] UninstallKeyPaths =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    /// <summary>
    /// #938's "Software installs" lane has no dedicated per-event log in this app today - unlike
    /// drivers (setupapi.dev.log) or updates (WindowsUpdateClient/Operational), Windows keeps no
    /// running log of application installs, and this app's existing SnapshotService (#93/#94) only
    /// captures a point-in-time name list with no per-item date, so a snapshot-diff can say
    /// "something changed" but not "when". This instead reads the registry Uninstall key's own
    /// optional InstallDate value (yyyyMMdd, no time-of-day) - the same Uninstall keys
    /// SnapshotService already walks for its name list, just additionally reading the date field
    /// that source walks past. Real per-application data (not fabricated), though not every
    /// installer sets InstallDate, so this lane is necessarily incomplete - entries with no
    /// parseable date are silently skipped rather than guessed at.
    /// </summary>
    public static List<TimelineEvent> GetSoftwareInstallEvents()
    {
        var events = new List<TimelineEvent>();
        foreach (var keyPath in UninstallKeyPaths)
        {
            try
            {
                using var uninstallKey = Registry.LocalMachine.OpenSubKey(keyPath);
                if (uninstallKey is null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = uninstallKey.OpenSubKey(subKeyName);
                        if (sub is null) continue;

                        string name = (sub.GetValue("DisplayName") as string ?? string.Empty).Trim();
                        if (name.Length == 0) continue;
                        if (sub.GetValue("SystemComponent") is int sc && sc == 1) continue;

                        string dateRaw = (sub.GetValue("InstallDate") as string ?? string.Empty).Trim();
                        if (!DateTime.TryParseExact(dateRaw, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var installed))
                            continue; // no usable date on this entry - can't place it on the timeline

                        string version = (sub.GetValue("DisplayVersion") as string ?? string.Empty).Trim();
                        events.Add(new TimelineEvent
                        {
                            Lane = TimelineLane.SoftwareInstalls,
                            Timestamp = installed,
                            Title = name,
                            Detail = version.Length > 0 ? $"Version {version}" : "Installed",
                            Source = $@"HKLM\{keyPath}\{subKeyName} (InstallDate)",
                            IsFailure = false,
                        });
                    }
                    catch { /* one malformed subkey shouldn't stop the rest of the scan */ }
                }
            }
            catch { /* registry hive/path unavailable */ }
        }
        return events;
    }

    #endregion

    #region 942 - Perf spikes lane (replayed CSV + spike detection)

    /// <summary>
    /// #942: turns one already-parsed LogReplayService result into "Perf spikes" lane markers,
    /// flagged with the *exact* mean/3-sigma outlier math SummaryViewModel.CheckAnomaly already
    /// uses for the live Health Check card (#67) - a rolling baseline over the preceding samples
    /// in the same series, not a second, different definition of "spike". Only called when a CSV
    /// log has actually been loaded/replayed this session (see TimelineViewModel.LoadAsync) -
    /// never auto-scans every historical log on disk.
    /// </summary>
    public static List<TimelineEvent> DetectPerfSpikes(LogReplayResult log)
    {
        var events = new List<TimelineEvent>();
        AppendSpikes(events, log, log.CpuPercent, "CPU");
        AppendSpikes(events, log, log.RamPercent, "Memory");
        AppendSpikes(events, log, log.DiskPercent, "Disk activity");
        return events;
    }

    // Mirrors SummaryViewModel.CheckAnomaly's own thresholds (>=20pt above the recent mean, and
    // >=3 standard deviations, with a 3.0 floor on the standard deviation so a near-flat baseline
    // doesn't make every tiny blip look like a spike) - applied as a sliding window across a
    // static array instead of "the last live sample vs. history so far", since a replayed log has
    // no live present moment.
    private static void AppendSpikes(List<TimelineEvent> events, LogReplayResult log, List<double> series, string label)
    {
        const int minBaseline = 10;
        const int maxBaseline = 30;

        for (int i = minBaseline; i < series.Count; i++)
        {
            int start = Math.Max(0, i - maxBaseline);
            int count = i - start;
            if (count < minBaseline) continue;

            double mean = 0;
            for (int j = start; j < i; j++) mean += series[j];
            mean /= count;

            double variance = 0;
            for (int j = start; j < i; j++) variance += (series[j] - mean) * (series[j] - mean);
            variance /= count;
            double std = Math.Max(Math.Sqrt(variance), 3.0);

            double current = series[i];
            if (current - mean >= 20 && current - mean >= 3 * std)
            {
                events.Add(new TimelineEvent
                {
                    Lane = TimelineLane.PerfSpikes,
                    Timestamp = log.Timestamps[i],
                    Title = $"{label} spike",
                    Detail = $"{label} jumped to {current:0}% (typically {mean:0}% over the preceding samples, {current - mean:0}pt above typical).",
                    Source = "Replayed CSV log (same outlier check as the Summary tab's Health Check)",
                    IsFailure = true,
                });
            }
        }
    }

    #endregion

    #region 944 - correlation scoring between lanes

    /// <summary>
    /// #944: for each Crashes/ServiceFailures marker, counts how many DriverInstalls/
    /// WindowsUpdates markers fall within +/-<paramref name="window"/> of it, then reports "N of
    /// your M crashes/failures happened within this window of a driver install/Windows Update" per
    /// change lane that has at least one match. This is explicitly a coincidence count, never a
    /// causation claim: the window is always stated in the headline, nothing here is ranked or
    /// labeled as "the cause", and the correlation is symmetric (a change shortly *after* a failure
    /// counts the same as one shortly before) precisely because this is coincidence-in-time, not a
    /// claimed causal direction. Software installs are deliberately left out of this scoring - see
    /// GetSoftwareInstallEvents' remarks on why that lane has no reliable per-event source today.
    /// </summary>
    public static List<TimelineCorrelationFinding> ComputeCorrelations(IReadOnlyList<TimelineEvent> events, TimeSpan window)
    {
        var failures = events.Where(e => e.Lane is TimelineLane.Crashes or TimelineLane.ServiceFailures).ToList();
        var findings = new List<TimelineCorrelationFinding>();
        if (failures.Count == 0) return findings;

        foreach (var lane in new[] { TimelineLane.DriverInstalls, TimelineLane.WindowsUpdates })
        {
            var changes = events.Where(e => e.Lane == lane).ToList();
            if (changes.Count == 0) continue;

            int matchCount = failures.Count(f => changes.Any(c => Math.Abs((c.Timestamp - f.Timestamp).TotalHours) <= window.TotalHours));
            if (matchCount == 0) continue;

            string laneLabel = lane == TimelineLane.DriverInstalls ? "a driver install" : "a Windows Update";
            findings.Add(new TimelineCorrelationFinding
            {
                Headline = $"{matchCount} of your {failures.Count} crash/failure event(s) happened within {window.TotalHours:0}h of {laneLabel} " +
                           "- a coincidence count over that time window, not a confirmed cause.",
                ChangeLane = lane,
                MatchCount = matchCount,
                TotalFailureCount = failures.Count,
                WindowHours = window.TotalHours,
            });
        }
        return findings.OrderByDescending(f => f.MatchCount).ToList();
    }

    #endregion
}
