using System.IO;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 17, items 56/57: enriches an already-parsed ApplicationCrashEvent list (EventLogService.
/// ReadApplicationCrashEvents, item 50) with the foreign-module flag and the injection-surface
/// cross-check - a separate pass over the already-read list rather than folded into the raw parse
/// itself, since it needs SignatureCheckService (a file-system signature read) and the injection-
/// surface scan (a registry + ShellExtensionService read), both meaningfully heavier than a plain
/// positional event-log parse and not needed by every caller (item 60's crash-count cache only
/// ever needs the raw AppName).
/// </summary>
public static class ApplicationCrashService
{
    /// <summary>Items 56/57: for every crash whose ModulePath lies outside both the crashing
    /// app's own install directory and the Windows system directories, attaches the module's
    /// signature status + vendor (item 56) and, when the module also matches a known machine-wide
    /// injection surface, a note to that effect (item 57). "Quick flag, not a verdict" per
    /// CLAUDE.md - see IsForeignModule for exactly how conservative the flag itself is.</summary>
    public static List<ApplicationCrashEvent> EnrichWithModuleForensics(List<ApplicationCrashEvent> events)
    {
        if (events.Count == 0) return events;

        var injectionSurfaces = LoadInjectionSurfaces();
        var result = new List<ApplicationCrashEvent>(events.Count);

        foreach (var e in events)
        {
            if (string.IsNullOrWhiteSpace(e.ModulePath) || string.IsNullOrWhiteSpace(e.ApplicationPath) ||
                !IsForeignModule(e.ApplicationPath, e.ModulePath))
            {
                result.Add(e);
                continue;
            }

            string sigStatus = SignatureCheckService.GetStatus(e.ModulePath);
            string? vendor = SignatureCheckService.GetVendor(e.ModulePath);

            string? injectionNote = injectionSurfaces.TryGetValue(Path.GetFileName(e.ModulePath), out var surfaceName)
                ? $"This DLL is a registered {surfaceName} - loaded into many processes, a likely common cause."
                : null;

            result.Add(e with
            {
                IsForeignModule = true,
                ForeignModuleReason = "Fault is in a module from another vendor, outside this application's own install directory.",
                ModuleSignatureStatus = sigStatus,
                ModuleVendor = vendor,
                InjectionSurfaceNote = injectionNote,
            });
        }
        return result;
    }

    /// <summary>Item 56: conservative "looks like injected/third-party code" check - a module is
    /// only flagged when its own directory is neither the crashing application's own directory
    /// (or a subdirectory of it) nor anywhere under %WINDIR% (System32/SysWOW64/WinSxS/etc., which
    /// every process legitimately loads dozens of system DLLs from). Wrapped to fail closed (never
    /// foreign) on any path-parsing error, per CLAUDE.md's "degrade to Unknown, never fabricate" -
    /// a missed flag is far less harmful here than a false positive on every ordinary crash.</summary>
    private static bool IsForeignModule(string applicationPath, string modulePath)
    {
        try
        {
            string? appDir = Path.GetDirectoryName(applicationPath);
            string? modDir = Path.GetDirectoryName(modulePath);
            if (string.IsNullOrEmpty(appDir) || string.IsNullOrEmpty(modDir)) return false;

            if (modDir.StartsWith(appDir, StringComparison.OrdinalIgnoreCase)) return false;

            string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windowsDir) && modDir.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Item 57: known injection-surface DLL file names on this machine, mapped to a
    /// friendly surface name - AppInit_DLLs (only when LoadAppInit_DLLs is actually enabled),
    /// every DLL ShellExtensionService already enumerates (item 20's own scan, reused rather than
    /// re-read), and Winlogon notification-package DLLs (the other classic system-wide DLL-
    /// injection surface, legacy but still checked by security tooling today). Keyed by bare file
    /// name (not full path) since a crash's own ModulePath and a registry entry naming the "same"
    /// DLL don't always agree on casing/environment-variable expansion, and a bare-name match is
    /// still a meaningful, if slightly looser, signal here. Best-effort throughout: a missing/
    /// inaccessible source just contributes nothing rather than failing the whole scan.</summary>
    private static Dictionary<string, string> LoadInjectionSurfaces()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows");
            if (key is not null)
            {
                bool enabled = key.GetValue("LoadAppInit_DLLs") is { } le && Convert.ToInt32(le) != 0;
                if (enabled && key.GetValue("AppInit_DLLs") is string dlls && !string.IsNullOrWhiteSpace(dlls))
                {
                    foreach (var raw in dlls.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var name = SafeFileName(raw.Trim().Trim('"'));
                        if (name is not null) result[name] = "AppInit_DLLs entry";
                    }
                }
            }
        }
        catch { /* key/values not present, or access denied - contributes nothing */ }

        try
        {
            foreach (var ext in ShellExtensionService.List())
            {
                var name = SafeFileName(ext.DllPath);
                if (name is not null) result[name] = $"shell extension ({ext.Category})";
            }
        }
        catch { /* best-effort - ShellExtensionService already degrades internally too */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Notify");
            if (key is not null)
            {
                foreach (var subName in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(subName);
                        var name = sub?.GetValue("DllName") is string dll ? SafeFileName(dll) : null;
                        if (name is not null) result[name] = "Winlogon notification package";
                    }
                    catch { /* one bad subkey shouldn't stop the rest */ }
                }
            }
        }
        catch { /* key unavailable (legacy, absent on many modern builds) - contributes nothing */ }

        return result;
    }

    private static string? SafeFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Item 52: per-application crash leaderboard, grouped by executable name (case-
    /// insensitive) - count, first/last seen, distinct faulting-module count, and the mean time
    /// between crashes. A pure derived aggregation over the already-parsed (and, by the time this
    /// runs, already-enriched) crash list, no new event-log query.</summary>
    public static List<AppCrashLeaderboardRow> BuildLeaderboard(List<ApplicationCrashEvent> events)
    {
        return events
            .Where(e => !string.IsNullOrWhiteSpace(e.AppName))
            .GroupBy(e => e.AppName!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ordered = g.OrderBy(e => e.TimeCreated).ToList();
                var first = ordered[0].TimeCreated;
                var last = ordered[^1].TimeCreated;
                double? mtbcHours = ordered.Count > 1 ? (last - first).TotalHours / (ordered.Count - 1) : null;

                return new AppCrashLeaderboardRow
                {
                    ExecutableName = g.Key,
                    Count = ordered.Count,
                    FirstSeen = first,
                    LastSeen = last,
                    DistinctFaultingModules = ordered
                        .Select(e => e.ModName)
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    MeanTimeBetweenCrashesHours = mtbcHours,
                };
            })
            .OrderByDescending(r => r.Count)
            .ThenByDescending(r => r.LastSeen)
            .ToList();
    }

    /// <summary>Item 60 support: crashes and hangs grouped by executable name (".exe" suffix
    /// stripped, matching ProcessRow.Name's own extension-less shape from Process.ProcessName) -
    /// built once per CrashHistoryCacheService refresh, not per Processes-tab poll tick. See
    /// CrashHistoryCacheService for the actual caching/staleness policy this feeds.</summary>
    public static Dictionary<string, int> BuildCrashCountsByExecutable(List<ApplicationCrashEvent> crashes, List<ApplicationHangEvent> hangs)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(string? name)
        {
            var key = NormalizeExeName(name);
            if (key is null) return;
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        foreach (var c in crashes) Add(c.AppName);
        foreach (var h in hangs) Add(h.ProcessName);

        return counts;
    }

    /// <summary>Strips a trailing ".exe" (case-insensitive) so a WER/event-log "notepad.exe" name
    /// matches ProcessRow.Name's own "notepad" (Process.ProcessName never carries the extension).
    /// Null in, null out - callers just skip a row with no name.</summary>
    public static string? NormalizeExeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
