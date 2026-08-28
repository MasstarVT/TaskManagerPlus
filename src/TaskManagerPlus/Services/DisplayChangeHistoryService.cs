using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #690: persisted log of display connect/disconnect/mode-change events - appended to
/// display-change-history.json from two sources: MainWindow's WM_DISPLAYCHANGE hook (in-app, live,
/// "Source": "App") and EventLogService.ReadMonitorPnpEvents' System-log scan (past sessions too,
/// "Source": "Kernel-PnP"). Same load/append/cap-and-save JSON shape as GpuHangHistoryService -
/// fails silently to "no history" on a missing or corrupt file. Repeated disconnects at a regular
/// interval - visible once this spans more than one session - point at a cable or a DisplayPort
/// link-training failure rather than a one-off.
/// </summary>
public static class DisplayChangeHistoryService
{
    private const int MaxEvents = 200;

    private static string SettingsPath => AppPaths.GetPath("display-change-history.json");

    public static List<DisplayChangeEvent> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<DisplayChangeEvent>>(json);
                if (list is not null) return list.OrderByDescending(e => e.TimeCreated).ToList();
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<DisplayChangeEvent>();
    }

    public static void Append(DisplayChangeEvent newEvent)
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
            // Best-effort - if we can't persist, this one event just won't show up next session.
        }
    }
}
