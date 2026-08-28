using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #979: persistence for the "queued for next boot/a chosen time" list - a small JSON array
/// (deferred-actions.json under AppPaths.SettingsDirectory), same "fails silently to defaults on a
/// missing/corrupt file" convention every other settings/log file in this app already follows (see
/// ChangeJournalService for the closest sibling - append-only-ish, static, no in-memory cache since
/// this is read rarely, only when the Changes panel's "Queued fixes" section is opened/refreshed).
/// Task creation/deletion itself goes through Services/ScheduledTaskService - this class only
/// tracks which scheduled task belongs to which remediation action for display purposes.
/// </summary>
public static class DeferredActionService
{
    private static string FilePath => AppPaths.GetPath("deferred-actions.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static List<DeferredAction> LoadAll()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<DeferredAction>();
            var json = File.ReadAllText(FilePath);
            var list = JsonSerializer.Deserialize<List<DeferredAction>>(json, JsonOpts);
            return list ?? new List<DeferredAction>();
        }
        catch
        {
            return new List<DeferredAction>();
        }
    }

    public static void Add(DeferredAction action)
    {
        try
        {
            var all = LoadAll();
            all.Add(action);
            Save(all);
        }
        catch
        {
            // Best-effort - the scheduled task itself (the thing that actually matters) is already
            // created by the time this is called; a failed tracking-file write shouldn't undo that.
        }
    }

    public static void Remove(string id)
    {
        try
        {
            var all = LoadAll();
            all.RemoveAll(a => string.Equals(a.Id, id, StringComparison.Ordinal));
            Save(all);
        }
        catch
        {
            // Best-effort.
        }
    }

    private static void Save(List<DeferredAction> all)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all, JsonOpts));
    }
}
