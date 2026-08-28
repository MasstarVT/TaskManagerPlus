using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>Loads and saves PollIntervalSettings to %AppData%\TaskManagerPlus\poll-intervals.json
/// (routed through AppPaths, so portable mode redirects it too) - same shape as ThemeService.
/// Each timer-owning ViewModel calls Load() fresh and Save() immediately after changing only its
/// own field (never keeping a long-lived cached copy), so two tabs' interval sliders changed in
/// the same session can never clobber each other's saved value - see EnergyThermalsViewModel/
/// PerformanceViewModel/ProcessesViewModel/ServicesViewModel's PollIntervalSeconds setters.</summary>
public static class PollIntervalSettingsService
{
    private static string SettingsPath => AppPaths.GetPath("poll-intervals.json");

    public static PollIntervalSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<PollIntervalSettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults.
        }
        return PollIntervalSettings.Defaults;
    }

    public static void Save(PollIntervalSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }
}
