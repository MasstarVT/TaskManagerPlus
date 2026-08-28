using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>#964: load/save for alerting.json (quiet hours + the per-rule alert-channel override
/// map) - fails silently to AlertingSettings' defaults on a missing/corrupt file.</summary>
public static class AlertingSettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string SettingsPath => AppPaths.GetPath("alerting.json");

    public static AlertingSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AlertingSettings>(json, JsonOpts);
                if (loaded is not null) return loaded;
            }
        }
        catch { /* corrupt/unreadable - fall back to defaults (quiet hours off, no overrides) */ }
        return new AlertingSettings();
    }

    public static void Save(AlertingSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch { /* best-effort */ }
    }
}
