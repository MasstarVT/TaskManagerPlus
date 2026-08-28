using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>#735: loads/saves fast-startup-prompt.json under AppPaths.SettingsDirectory - the
/// "dismiss this prompt for 7 days" state for the "you haven't fully restarted in N days" card.
/// Same fail-silently-to-defaults-on-missing/corrupt-file shape as every other settings file in
/// this app (see PollIntervalSettingsService's remarks).</summary>
public static class FastStartupPromptSettingsService
{
    private static string SettingsPath => AppPaths.GetPath("fast-startup-prompt.json");

    public static FastStartupPromptSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<FastStartupPromptSettings>(json);
                if (settings is not null) return settings;
            }
        }
        catch
        {
            // Corrupt/unreadable file - start from defaults rather than blocking the tab.
        }
        return new FastStartupPromptSettings();
    }

    public static void Save(FastStartupPromptSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // Best-effort - if we can't persist, the prompt just reappears next launch.
        }
    }

    /// <summary>#735: dismisses the prompt for 7 days from now.</summary>
    public static void DismissForSevenDays()
        => Save(new FastStartupPromptSettings { DismissedUntilUtc = DateTime.UtcNow.AddDays(7) });

    public static bool IsCurrentlyDismissed(FastStartupPromptSettings settings)
        => settings.DismissedUntilUtc is { } until && DateTime.UtcNow < until;
}
