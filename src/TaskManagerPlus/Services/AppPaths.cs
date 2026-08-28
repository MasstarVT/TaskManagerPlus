using System.IO;
using System.Text;
using System.Text.Json;

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
    private static string? _selectedMachine;

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

        // suggestions.md #998: "technician mode" - only relevant in portable mode (a USB build
        // carried between several client machines). Reads which machine's data folder was last
        // selected (persisted by SwitchMachine, below) - null (the default, unchanged behavior for
        // every portable build that's never touched this feature) means the flat, unpartitioned
        // portable Settings folder every prior round already used.
        if (_portable) _selectedMachine = LoadSelectedMachine();
    }

    /// <summary>The folder every settings/log/snapshot/report file should live under -
    /// %AppData%\TaskManagerPlus in normal mode (unchanged from every prior round); in portable
    /// mode, either &lt;exe folder&gt;\Settings directly (the flat default, also unchanged) or
    /// &lt;exe folder&gt;\Settings\Machines\&lt;machine&gt;\ once technician mode has a machine
    /// selected (#998). Every settings-persisting service in this app already computes its own
    /// path off this property (AppPaths.GetPath), typically once at construction - see
    /// <see cref="SwitchMachine"/>'s remarks for why switching machines needs an app restart to
    /// fully take effect.</summary>
    public static string SettingsDirectory
    {
        get
        {
            if (!_portable) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskManagerPlus");
            string flatPortableDir = Path.Combine(AppContext.BaseDirectory, "Settings");
            return _selectedMachine is { Length: > 0 } machine
                ? Path.Combine(flatPortableDir, "Machines", machine)
                : flatPortableDir;
        }
    }

    // ================================================================================
    // suggestions.md #998: technician mode - per-machine data folders on a portable USB build.
    // ================================================================================

    /// <summary>The fingerprint scheme is deliberately the simplest-honest one available: the
    /// sanitized machine name, not a hash of hardware identifiers - documented here rather than
    /// implied, since two machines that happen to share a computer name would collide. Good enough
    /// for its actual use (a human picking their own client's folder from a short list by name),
    /// not a cryptographic identity.</summary>
    public static string MachineFingerprint => SanitizeForFileSystem(Environment.MachineName);

    private static string PortableMachinesRoot => Path.Combine(AppContext.BaseDirectory, "Settings", "Machines");

    private static string TechnicianModeStatePath => Path.Combine(AppContext.BaseDirectory, "Settings", "technician-mode.json");

    /// <summary>Every machine folder currently registered under Settings\Machines\ - empty (not an
    /// error) outside portable mode, or before any machine has ever been registered. The Settings
    /// drawer's machine picker (portable-mode-only) lists these.</summary>
    public static List<string> ListMachines()
    {
        try
        {
            if (!_portable || !Directory.Exists(PortableMachinesRoot)) return new List<string>();
            return Directory.GetDirectories(PortableMachinesRoot)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>The currently active per-machine partition, or null when AppPaths currently points
    /// at the flat, unpartitioned portable Settings folder (the default).</summary>
    public static string? SelectedMachine => _selectedMachine;

    /// <summary>Registers (creates the folder for, if it doesn't exist yet) and switches to this
    /// physical machine's own partition - the Settings drawer's "Add this machine" button.</summary>
    public static void RegisterCurrentMachine()
    {
        if (!_portable) return;
        try { Directory.CreateDirectory(Path.Combine(PortableMachinesRoot, MachineFingerprint)); }
        catch { /* best-effort - SwitchMachine below still persists the selection even if the
                   folder create races/fails; GetPath's own callers create subfolders as needed. */ }
        SwitchMachine(MachineFingerprint);
    }

    /// <summary>Persists which machine's data folder AppPaths.SettingsDirectory should point into -
    /// pass null to go back to the flat, unpartitioned portable Settings folder. Deliberately does
    /// NOT hot-swap <see cref="_selectedMachine"/> for the running process: most settings-persisting
    /// services in this app already compute and cache their own file path (AppPaths.GetPath(...))
    /// once at construction time, long before this method could ever be called from a running
    /// Settings drawer - swapping the field here mid-session would still leave every already-
    /// constructed service writing to its old, now-stale path, which is worse than doing nothing at
    /// all. An app restart re-runs Initialize -> LoadSelectedMachine, which is the one point every
    /// service's path is (re)computed from a clean slate. This constraint is stated in the Settings
    /// drawer's own UI text, not just here.</summary>
    public static void SwitchMachine(string? machineName)
    {
        if (!_portable) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Settings"));
            string json = JsonSerializer.Serialize(new TechnicianModeState { SelectedMachine = machineName });
            File.WriteAllText(TechnicianModeStatePath, json);
        }
        catch { /* best-effort - if this can't persist, the picker just stays on its current selection */ }
    }

    private sealed class TechnicianModeState
    {
        public string? SelectedMachine { get; set; }
    }

    private static string? LoadSelectedMachine()
    {
        try
        {
            if (File.Exists(TechnicianModeStatePath))
            {
                var state = JsonSerializer.Deserialize<TechnicianModeState>(File.ReadAllText(TechnicianModeStatePath));
                if (!string.IsNullOrWhiteSpace(state?.SelectedMachine)) return state!.SelectedMachine;
            }
        }
        catch { /* corrupt/unreadable state file - fall back to the flat folder, same as every
                   other settings file in this app degrading to defaults */ }
        return null;
    }

    private static string SanitizeForFileSystem(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.Length == 0 ? "Unknown" : sb.ToString();
    }

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
