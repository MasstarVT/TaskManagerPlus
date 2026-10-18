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

    // Same range every PollIntervalSeconds setter clamps to.
    private const double MinSeconds = 0.5;
    private const double MaxSeconds = 10.0;

    public static PollIntervalSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<PollIntervalSettings>(json);
                if (settings is not null) return Clamped(settings);
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults.
        }
        return PollIntervalSettings.Defaults;
    }

    /// <summary>A hand-edited file can carry 0 or a negative interval, which the ViewModels' ctors
    /// feed straight into DispatcherTimer.Interval (negative throws, so the app wouldn't start;
    /// 0 ticks continuously) - clamp at load so all four timers are covered.</summary>
    private static PollIntervalSettings Clamped(PollIntervalSettings settings)
    {
        var defaults = PollIntervalSettings.Defaults;
        static double Clamp(double value, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, MinSeconds, MaxSeconds) : fallback;

        settings.ProcessesSeconds = Clamp(settings.ProcessesSeconds, defaults.ProcessesSeconds);
        settings.PerformanceSeconds = Clamp(settings.PerformanceSeconds, defaults.PerformanceSeconds);
        settings.ServicesSeconds = Clamp(settings.ServicesSeconds, defaults.ServicesSeconds);
        settings.EnergyThermalsSeconds = Clamp(settings.EnergyThermalsSeconds, defaults.EnergyThermalsSeconds);
        return settings;
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
