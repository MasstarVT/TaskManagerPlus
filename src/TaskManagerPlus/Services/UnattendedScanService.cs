using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// suggestions.md #997: sets up (and later reads back) a scheduled unattended diagnostic scan -
/// a Windows Scheduled Task running `TaskManagerPlus.exe --scan &lt;UnattendedScans folder&gt;
/// --quiet` nightly or weekly (ScheduledTaskService.CreateRecurringAsync), writing each run's
/// findings into its own `AppPaths.SettingsDirectory\UnattendedScans\&lt;yyyy-MM-dd_HHmmss&gt;\
/// findings.json` folder (see CliDumpService.ScanAsync's ResolveScanOutputPath for exactly how a
/// base-folder argument turns into a fresh dated subfolder on every run).
///
/// On next normal launch, MainViewModel calls <see cref="CheckAndMarkSeen"/> once - it compares
/// every scan folder newer than the last time this was called against a small persisted tracker
/// (`unattended-scan-tracker.json`: last-seen timestamp + the full rule-id set already reported),
/// using the same first-seen-by-rule-id comparison style FindingsHistoryService/SummaryViewModel's
/// own resolved/new-finding diffing already establishes, then immediately updates the tracker so
/// re-opening the app doesn't re-report the same scans. Returns null when there's nothing new to
/// report (no banner shown) - the common case between scheduled runs.
/// </summary>
public static class UnattendedScanService
{
    public const string TaskName = "TaskManagerPlus-NightlyScan";
    private const string DateFolderFormat = "yyyy-MM-dd_HHmmss";

    public static string UnattendedScansDirectory => AppPaths.GetPath("UnattendedScans");
    private static string TrackerPath => AppPaths.GetPath("unattended-scan-tracker.json");

    /// <summary>Creates (or replaces - schtasks /f overwrites) the scheduled task. `command` always
    /// points at this same running exe's own path plus `--scan &lt;UnattendedScansDirectory&gt;
    /// --quiet` (no `--scrub` by default - see the Settings drawer's own remarks on why that's an
    /// explicit opt-in, not a default, for an unattended run).</summary>
    public static Task<(bool Success, string? Error)> SetupScheduledScanAsync(ScheduledTaskFrequency frequency, TimeSpan timeOfDay, DayOfWeek dayOfWeek, bool scrub)
    {
        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;
        string command = scrub
            ? $"\"{exePath}\" --scan \"{UnattendedScansDirectory}\" --scrub --quiet"
            : $"\"{exePath}\" --scan \"{UnattendedScansDirectory}\" --quiet";
        return ScheduledTaskService.CreateRecurringAsync(TaskName, command, frequency, timeOfDay, dayOfWeek);
    }

    public static Task<(bool Success, string? Error)> RemoveScheduledScanAsync() => ScheduledTaskService.DeleteAsync(TaskName);

    private sealed class Tracker
    {
        public DateTime LastSeenUtc { get; set; } = DateTime.MinValue;
        public List<string> KnownRuleIds { get; set; } = new();
    }

    /// <summary>Call once per app launch. Returns a banner string like "2 unattended scans since
    /// you last looked, 3 new findings." or null when there's nothing to report (no scans folder
    /// yet, or every scan folder present was already reported on a prior launch).</summary>
    public static string? CheckAndMarkSeen()
    {
        var tracker = LoadTracker();
        try
        {
            if (!Directory.Exists(UnattendedScansDirectory)) return null;

            var allScanDirs = Directory.GetDirectories(UnattendedScansDirectory)
                .Select(d => (Dir: d, Time: ParseFolderTime(Path.GetFileName(d))))
                .Where(x => x.Time is not null)
                .OrderBy(x => x.Time)
                .ToList();

            var newScanDirs = allScanDirs.Where(x => x.Time > tracker.LastSeenUtc).ToList();

            // Recompute the full rule-id set across EVERY scan folder present (not just the new
            // ones) - what "already known" means for the NEXT check, regardless of whether this
            // pass has anything new to report.
            var allKnownRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var previouslyKnown = new HashSet<string>(tracker.KnownRuleIds, StringComparer.OrdinalIgnoreCase);
            var newRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (dir, _) in allScanDirs)
            {
                foreach (var ruleId in ReadRuleIds(dir))
                {
                    allKnownRuleIds.Add(ruleId);
                    if (!previouslyKnown.Contains(ruleId)) newRuleIds.Add(ruleId);
                }
            }

            var latest = allScanDirs.Count > 0 ? allScanDirs[^1].Time : tracker.LastSeenUtc;
            SaveTracker(new Tracker { LastSeenUtc = latest ?? tracker.LastSeenUtc, KnownRuleIds = allKnownRuleIds.ToList() });

            if (newScanDirs.Count == 0) return null;

            return $"{newScanDirs.Count} unattended scan{(newScanDirs.Count == 1 ? "" : "s")} since you last looked, " +
                   $"{newRuleIds.Count} new finding{(newRuleIds.Count == 1 ? "" : "s")}.";
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ReadRuleIds(string scanDir)
    {
        string path = Path.Combine(scanDir, "findings.json");
        if (!File.Exists(path)) yield break;

        List<HealthIssue>? findings = null;
        try { findings = JsonSerializer.Deserialize<List<HealthIssue>>(File.ReadAllText(path)); }
        catch { /* one malformed scan file shouldn't break the rest of the comparison */ }

        foreach (var f in findings ?? new List<HealthIssue>())
            if (f.RuleId is { Length: > 0 } id)
                yield return id;
    }

    private static DateTime? ParseFolderTime(string folderName) =>
        DateTime.TryParseExact(folderName, DateFolderFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;

    private static Tracker LoadTracker()
    {
        try
        {
            if (File.Exists(TrackerPath))
            {
                var loaded = JsonSerializer.Deserialize<Tracker>(File.ReadAllText(TrackerPath));
                if (loaded is not null) return loaded;
            }
        }
        catch { /* corrupt/missing tracker - fall back to defaults, same as every other settings file in this app */ }
        return new Tracker();
    }

    private static void SaveTracker(Tracker tracker)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.WriteAllText(TrackerPath, JsonSerializer.Serialize(tracker, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }
}
