using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #677: persisted log of detected "GPU engine flatline while a foreground app was running"
/// episodes - appended to gpu-hang-history.json by GpuViewModel's live per-tick flatline detector
/// (DetectEngineFlatline). Persisting these (rather than just an in-memory session list) is what
/// lets the Stability tab show pre-TDR hangs from past sessions too, not only the one currently
/// running - a hang that never escalated into an actual TDR/bugcheck would otherwise leave no trace
/// anywhere else in Windows. Same load/append/cap-and-save shape as PowerPlanHistoryService - fails
/// silently to an empty list on a missing or corrupt file.
/// </summary>
public static class GpuHangHistoryService
{
    private const int MaxEvents = 100;

    private static string SettingsPath => AppPaths.GetPath("gpu-hang-history.json");

    public static List<GpuHangEvent> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<GpuHangEvent>>(json);
                if (list is not null) return list.OrderByDescending(e => e.DetectedAt).ToList();
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<GpuHangEvent>();
    }

    public static List<GpuHangEvent> Append(GpuHangEvent newEvent)
    {
        var events = Load();
        events.Insert(0, newEvent);
        events = events.Take(MaxEvents).ToList();

        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the Stability-tab history just has one less entry.
        }

        return events;
    }
}
