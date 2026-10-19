using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>#947: loads/saves the Timeline panel's user-authored notes -
/// AppPaths.SettingsDirectory\timeline-notes.json, same plain-JSON/"defaults on missing or corrupt
/// file" shape every other settings file in this app uses (see AlertThresholdsService).</summary>
public static class TimelineNotesService
{
    private static string SettingsPath => AppPaths.GetPath("timeline-notes.json");

    public static List<TimelineNoteEntry> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var notes = JsonSerializer.Deserialize<List<TimelineNoteEntry>>(json);
                if (notes is not null)
                {
                    // TimelineViewModel reads Text.Length for every note - drop null notes and
                    // blank out null text from a hand edit.
                    var clean = notes.Where(n => n is not null).ToList();
                    if (clean.Count != notes.Count) AppPaths.MarkUnreadable(SettingsPath);
                    foreach (var n in clean) n.Text ??= string.Empty;
                    return clean;
                }
                AppPaths.MarkUnreadable(SettingsPath);
            }
        }
        catch
        {
            // Corrupt/unreadable file - degrade to "no notes yet", keeping a copy of the user's
            // notes for the next Save (which would otherwise write only the newest note).
            AppPaths.MarkUnreadable(SettingsPath);
        }
        return new List<TimelineNoteEntry>();
    }

    public static void Save(List<TimelineNoteEntry> notes)
    {
        try
        {
            if (!AppPaths.PreserveUnreadable(SettingsPath)) return;
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(notes, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, the note still shows for the rest of this session.
        }
    }
}
