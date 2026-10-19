using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
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

        // The task runs this exe as SYSTEM. From a folder a standard user can write to (a build
        // output folder, Downloads, a portable copy), any unelevated process could swap the exe or
        // a DLL beside it and get SYSTEM code execution on the next run - refuse instead.
        string? writable = FindNonAdminWriteAccess(exePath, includeSiblingBinaries: true);
        if (writable is not null)
            return Task.FromResult<(bool, string?)>((false,
                $"this copy of Task Manager Plus would run as SYSTEM, but {writable} - a standard user could swap in their own code. " +
                "Install it to an admin-only folder (for example under Program Files) and set up the scan from that copy."));

        string command = scrub
            ? $"\"{exePath}\" --scan \"{UnattendedScansDirectory}\" --scrub --quiet"
            : $"\"{exePath}\" --scan \"{UnattendedScansDirectory}\" --quiet";
        return ScheduledTaskService.CreateRecurringAsync(TaskName, command, frequency, timeOfDay, dayOfWeek);
    }

    public static Task<(bool Success, string? Error)> RemoveScheduledScanAsync() => ScheduledTaskService.DeleteAsync(TaskName);

    // SYSTEM, BUILTIN\Administrators, NT SERVICE\TrustedInstaller - the only principals allowed to
    // be able to change a binary this app runs with elevated/SYSTEM rights.
    private static readonly HashSet<string> TrustedWriterSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18",
        "S-1-5-32-544",
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
    };

    private const FileSystemRights FileWriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private const FileSystemRights DirectoryWriteRights =
        FileWriteRights | FileSystemRights.DeleteSubdirectoriesAndFiles;

    // An ancestor only needs guarding against having this folder renamed/deleted out from under it
    // or its ACL rewritten - plain "create folders" (which C:\ grants Authenticated Users) is fine.
    private const FileSystemRights AncestorWriteRights =
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>Null when <paramref name="filePath"/>, its folder and that folder's ancestors can
    /// only be modified by SYSTEM/Administrators/TrustedInstaller (owner and every allow ACE that
    /// applies to the object); otherwise a short reason. Fails closed - an unreadable ACL is a
    /// reason, not a pass. <paramref name="includeSiblingBinaries"/> also checks the .exe/.dll/.json
    /// files beside it (a framework-dependent app's code is the .dll next to the apphost).</summary>
    public static string? FindNonAdminWriteAccess(string filePath, bool includeSiblingBinaries = false)
    {
        try
        {
            var file = new FileInfo(filePath);
            var dir = file.Directory;
            if (!file.Exists || dir is null) return $"{filePath} couldn't be found";

            string? problem = CheckAcl(file.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), file.FullName, FileWriteRights);
            if (problem is not null) return problem;

            problem = CheckAcl(dir.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), dir.FullName, DirectoryWriteRights);
            if (problem is not null) return problem;

            if (includeSiblingBinaries)
            {
                foreach (var sibling in dir.EnumerateFiles())
                {
                    string ext = sibling.Extension;
                    if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    problem = CheckAcl(sibling.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), sibling.FullName, FileWriteRights);
                    if (problem is not null) return problem;
                }
            }

            for (var ancestor = dir.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                problem = CheckAcl(ancestor.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), ancestor.FullName, AncestorWriteRights);
                if (problem is not null) return problem;
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"the permissions on {filePath} couldn't be read ({ex.Message})";
        }
    }

    private static string? CheckAcl(FileSystemSecurity security, string path, FileSystemRights dangerousRights)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !TrustedWriterSids.Contains(owner.Value))
            return $"{path} is owned by {DescribeSid(owner)}, who can rewrite its permissions";

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue; // applies to children only
            if ((rule.FileSystemRights & dangerousRights) == 0) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && TrustedWriterSids.Contains(sid.Value)) continue;
            return $"{path} is writable by {DescribeSid(rule.IdentityReference)}";
        }
        return null;
    }

    private static string DescribeSid(IdentityReference id)
    {
        try { return id.Translate(typeof(NTAccount)).Value; }
        catch { return id.Value; }
    }

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
