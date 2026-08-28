using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #664: persisted log of active-power-scheme changes, appended to power-plan-history.json
/// whenever EnergyThermalsViewModel notices the active scheme GUID changed since its last check
/// (see the view-model's CheckPowerPlanChangeIfDueAsync). Vendor utilities and games are known to
/// call `powercfg /setactive` behind the user's back, so a change this app itself didn't originate
/// (via SetPowerPlanCommand) is exactly the "my settings keep reverting" signal worth a timestamped
/// trail for. Same load/append/cap-and-save shape as StandbyDrainService/ThrottleHistoryService -
/// fails silently to an empty list on a missing or corrupt file.
/// </summary>
public static class PowerPlanHistoryService
{
    private const int MaxEvents = 100;

    private static string SettingsPath => AppPaths.GetPath("power-plan-history.json");

    public static List<PowerPlanChangeEvent> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<PowerPlanChangeEvent>>(json);
                if (list is not null) return list.OrderByDescending(e => e.Timestamp).ToList();
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<PowerPlanChangeEvent>();
    }

    public static List<PowerPlanChangeEvent> Append(PowerPlanChangeEvent newEvent)
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
            // Best-effort - if we can't persist, the trend just has less history to work with.
        }

        return events;
    }
}
