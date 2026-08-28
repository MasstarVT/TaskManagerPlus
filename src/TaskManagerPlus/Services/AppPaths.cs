using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 12, #87: single source of truth for "where does this app's persisted state live" -
/// every settings-persisting service in this app previously hardcoded
/// <c>Environment.GetFolderPath(SpecialFolder.ApplicationData)\TaskManagerPlus</c> independently
/// (ThemeService, AlertThresholdsService, DashboardLayoutService, LoggingSettingsService,
/// NetworkHistoryService, RemoteMonitorSettingsService, SummarySettingsService,
/// UiPreferencesService, BootPerformanceService, plus a few inline paths in SummaryViewModel/
/// LoggingViewModel for the Snapshots/Logs/Reports folders) - centralizing it here means
/// portable mode is a one-flag decision made once at startup (<see cref="Initialize"/>, called
/// from App.xaml.cs before any service or ViewModel is constructed) rather than a
/// find-and-replace across a dozen files each time a new settings file is added.
///
/// Portable mode redirects everything to a "Settings" folder next to the exe instead of
/// %AppData%\TaskManagerPlus - useful for running the app off a USB drive without leaving
/// anything behind on the host machine's profile. Triggered by either the `--portable` launch
/// flag or a `portable.marker` file dropped next to the exe (so a portable USB copy can be made
/// to "just work" without needing to edit a shortcut's target every time).
/// </summary>
public static class AppPaths
{
    private static bool _portable;
    private static bool _initialized;

    /// <summary>True once <see cref="Initialize"/> has run and portable mode was selected -
    /// exposed so the Settings drawer / About area can tell the user which mode is active.</summary>
    public static bool IsPortable => _portable;

    /// <summary>Must be called exactly once, from App.xaml.cs, before any settings file is
    /// read or written. Safe to call more than once (later calls are ignored) so an accidental
    /// second call from a service's own static initializer can't flip the mode mid-session.</summary>
    public static void Initialize(string[] args)
    {
        if (_initialized) return;
        _initialized = true;

        bool flagged = args.Any(a => a.Equals("--portable", StringComparison.OrdinalIgnoreCase));
        bool markerPresent = false;
        try
        {
            markerPresent = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.marker"));
        }
        catch { /* best-effort - a failed marker check just means "not portable" */ }

        _portable = flagged || markerPresent;
    }

    /// <summary>The folder every settings/log/snapshot/report file should live under - either
    /// %AppData%\TaskManagerPlus (normal mode, unchanged from every prior round) or
    /// &lt;exe folder&gt;\Settings (portable mode).</summary>
    public static string SettingsDirectory => _portable
        ? Path.Combine(AppContext.BaseDirectory, "Settings")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskManagerPlus");

    /// <summary>Combines one or more path segments onto <see cref="SettingsDirectory"/> - e.g.
    /// <c>AppPaths.GetPath("theme.json")</c> or <c>AppPaths.GetPath("Logs", "rolling-buffer.csv")</c>.
    /// Does not create the directory - callers already each do that (or SaveFileDialog/File.WriteAllText
    /// does it for them) right before writing, the same as before this helper existed.</summary>
    public static string GetPath(params string[] segments)
    {
        var parts = new string[segments.Length + 1];
        parts[0] = SettingsDirectory;
        Array.Copy(segments, 0, parts, 1, segments.Length);
        return Path.Combine(parts);
    }
}
