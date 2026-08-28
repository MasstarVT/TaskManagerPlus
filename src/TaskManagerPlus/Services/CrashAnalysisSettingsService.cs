using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>Round 14, item 25: loads/saves CrashAnalysisSettings to
/// %AppData%\TaskManagerPlus\crash-analysis.json (via AppPaths, so portable mode redirects it
/// too) - same shape as PollIntervalSettingsService.</summary>
public static class CrashAnalysisSettingsService
{
    private static string SettingsPath => AppPaths.GetPath("crash-analysis.json");

    public static CrashAnalysisSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<CrashAnalysisSettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt/unreadable settings file - fall back to defaults.
        }
        return CrashAnalysisSettings.Defaults;
    }

    public static void Save(CrashAnalysisSettings settings)
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
