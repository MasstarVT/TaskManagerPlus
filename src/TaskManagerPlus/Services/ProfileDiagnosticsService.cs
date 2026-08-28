using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Profile load duration (#720), temp/corrupt profile detection (#721), roaming profile audit
/// (#722), and registry-handle-leak/slow-logoff detection (#723) - the "Profile health" sub-card
/// of the Startup tab's Sign-in section. Same adaptive event-field-reading and degrade-to-empty
/// pattern as LogonDiagnosticsService/BootPerformanceService.
/// </summary>
public static class ProfileDiagnosticsService
{
    private const int LookbackDays = 30;

    #region #720 - profile load duration

    private const string ProfileServiceLogName = "Microsoft-Windows-User Profile Service/Operational";
    private const int ProfileLoadStartEventId = 1;
    private const int ProfileLoadEndEventId = 2;
    private const int MaxProfileServiceEvents = 500;

    /// <summary>#720: pairs Microsoft-Windows-User Profile Service/Operational events 1 (load
    /// start) and 2 (load end) - the timestamp delta between a matching pair is the profile load
    /// time. Paired by whatever user-identifying field (SID/account name) the events carry when
    /// present; falls back to simple chronological (i-th start with i-th end) pairing when no such
    /// field is found, since a single machine typically has at most a handful of profile loads in
    /// flight at once.</summary>
    public static List<ProfileLoadTiming> ReadProfileLoadTimings()
    {
        var results = new List<ProfileLoadTiming>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery(ProfileServiceLogName, PathType.LogName,
                $"*[System[(EventID={ProfileLoadStartEventId} or EventID={ProfileLoadEndEventId}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]");
            using var reader = new EventLogReader(query);

            var starts = new List<(string? Key, DateTime Time)>();
            var ends = new List<(string? Key, DateTime Time)>();

            int count = 0;
            while (count < MaxProfileServiceEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is not { } time) continue;
                    var key = ExtractUserKey(record);
                    if (record.Id == ProfileLoadStartEventId) starts.Add((key, time));
                    else ends.Add((key, time));
                }
            }

            bool haveKeys = starts.Any(s => s.Key is not null) && ends.Any(e => e.Key is not null);
            if (haveKeys)
            {
                var usedEnds = new HashSet<int>();
                foreach (var start in starts.OrderBy(s => s.Time))
                {
                    int matchIdx = -1;
                    DateTime bestTime = DateTime.MaxValue;
                    for (int i = 0; i < ends.Count; i++)
                    {
                        if (usedEnds.Contains(i)) continue;
                        var end = ends[i];
                        if (end.Key != start.Key || end.Time < start.Time) continue;
                        if (end.Time < bestTime) { bestTime = end.Time; matchIdx = i; }
                    }
                    if (matchIdx < 0) continue;
                    usedEnds.Add(matchIdx);
                    results.Add(new ProfileLoadTiming { UserKey = start.Key ?? "(unknown)", LoadStart = start.Time, LoadEnd = ends[matchIdx].Time });
                }
            }
            else
            {
                // No usable key field on either side - pair chronologically instead.
                var orderedStarts = starts.OrderBy(s => s.Time).ToList();
                var orderedEnds = ends.OrderBy(e => e.Time).ToList();
                int n = Math.Min(orderedStarts.Count, orderedEnds.Count);
                for (int i = 0; i < n; i++)
                {
                    if (orderedEnds[i].Time < orderedStarts[i].Time) continue;
                    results.Add(new ProfileLoadTiming { UserKey = "(current user)", LoadStart = orderedStarts[i].Time, LoadEnd = orderedEnds[i].Time });
                }
            }
        }
        catch
        {
            // Channel unavailable/access denied - no profile load timing data.
        }
        return results.OrderByDescending(r => r.LoadStart).ToList();
    }

    private static string? ExtractUserKey(EventRecord record)
    {
        try
        {
            return record.UserId?.Value;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region #720 - on-demand profile size/file-count walk

    private const long MaxFilesToWalk = 400_000;
    private static readonly TimeSpan MaxWalkDuration = TimeSpan.FromSeconds(25);

    /// <summary>#720: on-demand (gated behind an explicit button - never on a tick, per this app's
    /// on-demand-vs-polled convention) recursive size/file-count walk of a profile folder, so an
    /// enormous AppData is visible as a candidate reason for a slow profile load. Bounded by both a
    /// file-count and a wall-clock budget (same "depth/size safety cap so one pathological folder
    /// can't turn an on-demand scan into a multi-minute hang" tradeoff LargestItemsService already
    /// takes) - TotalBytes/FileCount become a lower bound, not a guess, if either cap is hit.</summary>
    public static ProfileSizeInfo ComputeProfileSize(string profileImagePath)
    {
        if (string.IsNullOrWhiteSpace(profileImagePath) || !Directory.Exists(profileImagePath))
            return new ProfileSizeInfo { ProfileImagePath = profileImagePath, Error = "Folder not found." };

        try
        {
            var sw = Stopwatch.StartNew();
            long totalBytes = 0, fileCount = 0;
            bool truncated = false;
            WalkForSize(new DirectoryInfo(profileImagePath), sw, ref totalBytes, ref fileCount, ref truncated);

            return new ProfileSizeInfo
            {
                ProfileImagePath = profileImagePath,
                TotalBytes = totalBytes,
                FileCount = fileCount,
                TruncatedByBudget = truncated,
            };
        }
        catch (Exception ex)
        {
            return new ProfileSizeInfo { ProfileImagePath = profileImagePath, Error = ex.Message };
        }
    }

    private static void WalkForSize(DirectoryInfo dir, Stopwatch sw, ref long totalBytes, ref long fileCount, ref bool truncated)
    {
        if (truncated) return;
        if (fileCount >= MaxFilesToWalk || sw.Elapsed >= MaxWalkDuration) { truncated = true; return; }

        foreach (var file in SafeEnumerateFiles(dir))
        {
            if (fileCount >= MaxFilesToWalk || sw.Elapsed >= MaxWalkDuration) { truncated = true; return; }
            try { totalBytes += file.Length; fileCount++; } catch { /* vanished mid-enumeration - skip */ }
        }

        foreach (var sub in SafeEnumerateDirectories(dir))
        {
            WalkForSize(sub, sw, ref totalBytes, ref fileCount, ref truncated);
            if (truncated) return;
        }
    }

    private static IEnumerable<FileInfo> SafeEnumerateFiles(DirectoryInfo dir)
    {
        try { return dir.EnumerateFiles(); }
        catch { return Enumerable.Empty<FileInfo>(); } // access denied / IO error
    }

    private static IEnumerable<DirectoryInfo> SafeEnumerateDirectories(DirectoryInfo dir)
    {
        try { return dir.EnumerateDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint)); }
        catch { return Enumerable.Empty<DirectoryInfo>(); }
    }

    #endregion

    #region #721/#722 - ProfileList registry scan (temp/corrupt + roaming)

    private const string ProfileListKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// <summary>#721/#722: scans every SID subkey of HKLM\...\ProfileList - Windows' own inventory
    /// of profiles it knows about - for a ".bak"-suffixed SID (a temporary-profile marker Windows
    /// creates itself), a nonzero State flag, a RefCount stuck above zero, a ProfileImagePath that
    /// no longer resolves, and a CentralProfile value (roaming profile UNC path). Never fabricates
    /// a flag: a missing/unreadable value is null (Unknown), not assumed healthy or unhealthy.</summary>
    public static List<ProfileListEntry> ReadProfileListEntries()
    {
        var results = new List<ProfileListEntry>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ProfileListKeyPath);
            if (key is null) return results;

            foreach (var sidName in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(sidName);
                    if (sub is null) continue;

                    string? rawPath = sub.GetValue("ProfileImagePath") as string;
                    string? expandedPath = string.IsNullOrEmpty(rawPath) ? null : Environment.ExpandEnvironmentVariables(rawPath);
                    string? centralProfile = sub.GetValue("CentralProfile") as string;

                    results.Add(new ProfileListEntry
                    {
                        Sid = sidName,
                        IsBakSuffixed = sidName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase),
                        State = sub.GetValue("State") as int?,
                        RefCount = sub.GetValue("RefCount") as int?,
                        ProfileImagePath = expandedPath,
                        ProfileImagePathExists = expandedPath is not null && DirectoryExistsSafe(expandedPath),
                        CentralProfile = string.IsNullOrWhiteSpace(centralProfile) ? null : centralProfile,
                    });
                }
                catch
                {
                    // One malformed/inaccessible SID subkey shouldn't stop the rest of the scan.
                }
            }
        }
        catch
        {
            // Key unavailable/access denied - an empty list, same degrade-to-nothing pattern
            // every other registry read in this app uses.
        }
        return results;
    }

    private static bool DirectoryExistsSafe(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    #endregion

    #region #721/#722/#723 - User Profile Service (Application log) diagnostic events

    private const string ProfileServiceProviderName = "Microsoft-Windows-User Profile Service";
    private static readonly int[] ProfileServiceEventIds = { 1500, 1502, 1511, 1515, 1509, 1521, 1530 };
    private const int MaxProfileAppEvents = 500;

    // #723: event 1530's message lists each process still holding the leaked hive as
    // "Process <pid> (<image path>)" - not a documented, versioned insertion-string layout, so
    // this is a best-effort regex over the rendered message text (same tradeoff
    // EventLogService.FaultingModuleRegex already takes for a different event), not a guaranteed
    // exact parse.
    private static readonly Regex LeakedProcessRegex = new(@"Process\s+\d+\s*\(([^)]+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>#721 (1500/1502/1511/1515 - temp/corrupt profile family), #722 (1509/1521 -
    /// roaming copy/sync errors), and #723 (1530 - registry hive still in use, i.e. the usual
    /// cause of a slow sign-out/shutdown) all read from the same Application-log provider, so
    /// they're gathered in one query and split out by EventId by the callers/ViewModel.</summary>
    public static List<ProfileServiceEventEntry> ReadProfileServiceEvents()
    {
        var results = new List<ProfileServiceEventEntry>();
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            string idFilter = string.Join(" or ", ProfileServiceEventIds.Select(id => $"EventID={id}"));
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='{ProfileServiceProviderName}'] and ({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);

            int count = 0;
            while (count < MaxProfileAppEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    if (record.TimeCreated is not { } time) continue;

                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    var processNames = record.Id == 1530
                        ? LeakedProcessRegex.Matches(message).Select(m => Path.GetFileName(m.Groups[1].Value.Trim())).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                        : new List<string>();

                    results.Add(new ProfileServiceEventEntry
                    {
                        TimeCreated = time,
                        EventId = record.Id,
                        Message = string.IsNullOrWhiteSpace(message) ? "(no further detail available)" : Truncate(message, 400),
                        LeakedProcessNames = processNames,
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable/access denied - an empty list, same degrade-to-nothing
            // pattern every other event-log read in this app uses.
        }
        return results;
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";

    #endregion
}
