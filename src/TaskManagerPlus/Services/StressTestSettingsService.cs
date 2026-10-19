using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #699: persists the Stress test panel's pass/fail and safety-abort criteria to
/// %AppData%\TaskManagerPlus\stress-test.json - the canonical small-settings-file pattern (see
/// PsuSettingsService/ThemeService's own remarks), fails silently to StressTestSettings' defaults
/// on a missing/corrupt file.
/// </summary>
public static class StressTestSettingsService
{
    private static string SettingsPath => AppPaths.GetPath("stress-test.json");

    public static StressTestSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<StressTestSettings>(json);
                if (settings is not null) return Sanitize(settings);
                AppPaths.MarkUnreadable(SettingsPath);
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to defaults, keeping a copy of the
            // user's file (it holds a custom temperature ceiling) for the next Save.
            AppPaths.MarkUnreadable(SettingsPath);
        }
        return new StressTestSettings();
    }

    /// <summary>StressTestViewModel's getters hand these straight to the run (thread count,
    /// share of free RAM to allocate), so a hand-edited file is clamped to the same ranges its
    /// setters enforce. 0 threads and a 0 ceiling keep their "auto" meaning.</summary>
    private static StressTestSettings Sanitize(StressTestSettings settings)
    {
        settings.DefaultDurationSeconds = Math.Clamp(settings.DefaultDurationSeconds, 10, 3600);
        settings.CpuThreadCount = settings.CpuThreadCount <= 0
            ? 0
            : Math.Min(settings.CpuThreadCount, Environment.ProcessorCount * 2);
        settings.MemoryTestShareOfFreePercent = Math.Clamp(settings.MemoryTestShareOfFreePercent, 5, 70);
        settings.TempCeilingC = Math.Max(0, settings.TempCeilingC);
        return settings;
    }

    public static void Save(StressTestSettings settings)
    {
        try
        {
            if (!AppPaths.PreserveUnreadable(SettingsPath)) return;
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            AppPaths.WriteAllTextAtomic(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }
}
