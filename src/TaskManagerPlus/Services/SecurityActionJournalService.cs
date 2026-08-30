using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 20, #899(d): loads and saves the list of SecurityActionJournalEntry to
/// %AppData%\TaskManagerPlus\security-actions.json (routed through AppPaths, same as every other
/// settings file) - same load/save/fail-silently-to-defaults shape as PollIntervalSettingsService/
/// ThemeService. Unlike those, this file holds a growing LIST rather than one settings object, so
/// Append/MarkUndone read-modify-write the whole file rather than exposing a bare Save(T) - still
/// the same "small JSON file, fails silently" contract everywhere else in this app uses.
/// </summary>
public static class SecurityActionJournalService
{
    private static string JournalPath => AppPaths.GetPath("security-actions.json");
    private const int MaxEntries = 500; // a growing-forever journal would eventually be unusable to scroll - oldest entries drop first

    public static List<SecurityActionJournalEntry> Load()
    {
        try
        {
            if (File.Exists(JournalPath))
            {
                var json = File.ReadAllText(JournalPath);
                var entries = JsonSerializer.Deserialize<List<SecurityActionJournalEntry>>(json);
                if (entries is not null) return entries;
            }
        }
        catch
        {
            // Corrupt or unreadable journal file - fall back to an empty journal, same as every
            // other settings file in this app degrades on a bad read.
        }
        return new List<SecurityActionJournalEntry>();
    }

    private static void Save(List<SecurityActionJournalEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(JournalPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            // #1026: write-to-temp-then-rename so a crash mid-write can't leave partial JSON that
            // Load silently degrades to an empty journal, destroying the whole action history.
            string tempPath = JournalPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, JournalPath, overwrite: true);
        }
        catch
        {
            // Best-effort - if we can't persist, the action itself still already happened; the
            // Undo button for THIS session's in-memory copy still works even if the write failed.
        }
    }

    /// <summary>Appends one entry and persists - returns the full (possibly-trimmed) list so
    /// callers can reset their in-memory ObservableCollection from the authoritative saved state.</summary>
    public static List<SecurityActionJournalEntry> Append(List<SecurityActionJournalEntry> current, SecurityActionJournalEntry entry)
    {
        var updated = new List<SecurityActionJournalEntry>(current) { entry };
        if (updated.Count > MaxEntries) updated = updated.Skip(updated.Count - MaxEntries).ToList();
        Save(updated);
        return updated;
    }

    public static List<SecurityActionJournalEntry> MarkUndone(List<SecurityActionJournalEntry> current, string entryId)
    {
        var updated = current.Select(e => e.Id == entryId ? CloneUndone(e) : e).ToList();
        Save(updated);
        return updated;
    }

    private static SecurityActionJournalEntry CloneUndone(SecurityActionJournalEntry e) => new()
    {
        Id = e.Id,
        TimestampUtc = e.TimestampUtc,
        Kind = e.Kind,
        ActionDescription = e.ActionDescription,
        Target = e.Target,
        UndoPayload = e.UndoPayload,
        IsUndone = true,
    };

    // ==================================================================================
    // Kind-specific UndoPayload builders/readers - a tiny JSON object per Kind, so
    // SecurityViewModel.UndoJournalEntry knows exactly what fields to expect for each.
    // ==================================================================================

    public static string BuildFirewallUndoPayload(string ruleName) => JsonSerializer.Serialize(new { ruleName });
    public static string BuildServiceUndoPayload(string serviceName, string previousStartType) => JsonSerializer.Serialize(new { serviceName, previousStartType });
    public static string BuildScheduledTaskUndoPayload(string taskName) => JsonSerializer.Serialize(new { taskName });
    public static string BuildStartupItemUndoPayload(string name, string source, string command) => JsonSerializer.Serialize(new { name, source, command });
    public static string BuildQuarantineUndoPayload(string quarantinePath, string originalPath) => JsonSerializer.Serialize(new { quarantinePath, originalPath });

    public static Dictionary<string, string>? ParseUndoPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(payload); }
        catch { return null; }
    }
}
