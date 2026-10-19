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
                if (settings is { Rules: not null })
                {
                    // ResponsivenessViewModel evaluates every rule on its tick - a null rule would
                    // throw there, and an integer Kind the enum doesn't define (the string-enum
                    // converter accepts integers) could never trip.
                    var rules = settings.Rules.Where(r => r is not null && Enum.IsDefined(r.Kind)).ToList();
                    if (rules.Count != settings.Rules.Count) AppPaths.MarkUnreadable(SettingsPath);
                    settings.Rules = rules;
                    return settings;
                }
                AppPaths.MarkUnreadable(SettingsPath);
            }
        }
        catch
        {
            // Corrupt/unreadable settings file - fall back to defaults, keeping a copy of the
            // user's file for the next Save.
            AppPaths.MarkUnreadable(SettingsPath);
        }
        return StutterTriggerRulesSettings.Defaults;
    }

    public static void Save(StutterTriggerRulesSettings settings)
    {
        try
        {
            if (!AppPaths.PreserveUnreadable(SettingsPath)) return;
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Best-effort - if we can't persist, the rules just won't survive a restart.
        }
    }
}
