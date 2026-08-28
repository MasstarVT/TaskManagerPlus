using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace TaskManagerPlus.Services;

/// <summary>
/// #980: a single app-wide "read-only mode" switch - "for diagnosing someone else's machine, or a
/// technician's initial assessment pass." Static/singleton like every other small settings service
/// in this app (fails silently to false on a missing/corrupt read-only-mode.json), but unlike a
/// plain settings file this one is read from a CanExecute predicate on every mutating RelayCommand
/// across several ViewModels (Services/Startup/Processes/EnergyThermals/RemediationReview), so
/// flipping it has to make every one of those commands re-query its enabled state immediately -
/// done by piggybacking on the same CommandManager.InvalidateRequerySuggested() every RelayCommand/
/// AsyncRelayCommand in this app already wires its own CanExecuteChanged to
/// (Common/RelayCommand.cs), rather than adding a second per-ViewModel event subscription
/// mechanism just for this one setting.
/// </summary>
public static class ReadOnlyModeService
{
    private sealed class ReadOnlyModeSettings
    {
        public bool IsReadOnly { get; set; }
    }

    private static string SettingsPath => AppPaths.GetPath("read-only-mode.json");

    private static bool? _cached;

    /// <summary>Loaded lazily on first read (mirrors AlertThresholdsService's own field-initializer
    /// pattern, just deferred since AppPaths.Initialize must run first and this is a static class
    /// with no constructor to order that against).</summary>
    public static bool IsReadOnly
    {
        get => _cached ??= Load();
        set
        {
            if (_cached == value) return;
            _cached = value;
            Save(value);
            // Every RelayCommand/AsyncRelayCommand in this app hooks CanExecuteChanged straight to
            // CommandManager.RequerySuggested (see Common/RelayCommand.cs) - this one call is
            // enough to make every mutating command across every open tab re-evaluate its
            // CanExecute immediately, without this service needing to know which ViewModels exist.
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static bool Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<ReadOnlyModeSettings>(json);
            return settings?.IsReadOnly ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static void Save(bool value)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new ReadOnlyModeSettings { IsReadOnly = value }));
        }
        catch
        {
            // Best-effort - worst case the toggle doesn't survive a restart this one time.
        }
    }
}
