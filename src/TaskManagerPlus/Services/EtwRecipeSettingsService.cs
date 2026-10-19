using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>#158: loads/saves EtwRecipeSettings to %AppData%\TaskManagerPlus\etw-recipes.json
/// (routed through AppPaths, so portable mode redirects it too) - same load/save shape as
/// PollIntervalSettingsService/EventFilterSettingsService. A missing/corrupt file, or one that
/// somehow deserializes to an empty recipe list, falls back to EtwRecipeSettings.Defaults' built-in
/// recipes (seeded from #150's own WPR scenario presets plus one netsh trace example) - the same
/// silent-fallback-to-defaults rule every settings file in this app follows.</summary>
public static class EtwRecipeSettingsService
{
    private static string SettingsPath => AppPaths.GetPath("etw-recipes.json");

    public static EtwRecipeSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<EtwRecipeSettings>(json);
                // Explicit nulls survive deserialization - drop null recipes and blank out null
                // text so the recipe list and RunRecipeAsync never dereference one.
                var recipes = settings?.Recipes?.Where(r => r is not null).ToList();
                if (recipes is not null && recipes.Count != settings!.Recipes.Count) AppPaths.MarkUnreadable(SettingsPath);
                if (recipes is { Count: > 0 })
                {
                    foreach (var r in recipes)
                    {
                        r.Name ??= string.Empty;
                        r.Description ??= string.Empty;
                        r.Tool ??= string.Empty;
                        r.Arguments ??= string.Empty;
                        r.ExpectedSizePerMinute ??= string.Empty;
                    }
                    settings!.Recipes = recipes;
                    return settings;
                }
                if (recipes is null) AppPaths.MarkUnreadable(SettingsPath);
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults, keeping a copy of the
            // user's file for the next Save.
            AppPaths.MarkUnreadable(SettingsPath);
        }
        return EtwRecipeSettings.Defaults;
    }

    public static void Save(EtwRecipeSettings settings)
    {
        try
        {
            if (!AppPaths.PreserveUnreadable(SettingsPath)) return;
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }
}
