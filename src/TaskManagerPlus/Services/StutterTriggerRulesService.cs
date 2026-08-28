using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>#297/#298: loads/saves the stutter-trigger rule list (plus #298's ETW circular-capture
/// opt-in toggle) to stutter-trigger-rules.json under AppPaths.SettingsDirectory - same load/save-
/// with-defaults-on-failure shape as every other settings file in this app. Enums are serialized
/// as their name (JsonStringEnumConverter) rather than a raw int, so the file stays readable/
/// hand-editable, matching the spirit of ThemeColors.ThemeMode being stored as a string.</summary>
public static class StutterTriggerRulesService
{
    private static string SettingsPath => AppPaths.GetPath("stutter-trigger-rules.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static StutterTriggerRulesSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<StutterTriggerRulesSettings>(json, JsonOptions);
                // A deliberately-saved empty rule list (the user removed every rule) is respected
                // as-is - only a null Rules (a genuinely malformed/older-shaped file) falls back to
                // the factory defaults below.
                if (settings is { Rules: not null }) return settings;
            }
        }
        catch
        {
            // Corrupt/unreadable settings file - fall back to defaults.
        }
        return StutterTriggerRulesSettings.Defaults;
    }

    public static void Save(StutterTriggerRulesSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Best-effort - if we can't persist, the rules just won't survive a restart.
        }
    }
}
