using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #624: persists the one user-entered PSU wattage to %AppData%\TaskManagerPlus\psu.json - the
/// canonical small-settings-file pattern (see ThermalBaselineService/ThrottleHistoryService's own
/// remarks), used as the sanity-check denominator whenever WMI doesn't report a wattage itself
/// (PsuService - the common case on a self-built desktop).
/// </summary>
public static class PsuSettingsService
{
    private static string SettingsPath => AppPaths.GetPath("psu.json");

    public static PsuSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<PsuSettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults (no user wattage set).
        }
        return new PsuSettings();
    }

    public static void Save(PsuSettings settings)
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
