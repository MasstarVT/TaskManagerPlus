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
                // Valid JSON can still hold null rows or null ids - drop those rows only.
                if (entries is not null)
                {
                    var kept = entries.Where(e => e is not null && !string.IsNullOrEmpty(e.Id)).ToList();
                    if (kept.Count != entries.Count) AppPaths.MarkUnreadable(JournalPath);
                    foreach (var e in kept) e.IsVerified = Verify(e);
                    return kept;
                }
                AppPaths.MarkUnreadable(JournalPath);
            }
        }
        catch
        {
            // Corrupt or unreadable journal file - fall back to an empty journal for this session,
            // and keep a copy of the file before the next Save replaces it.
            AppPaths.MarkUnreadable(JournalPath);
        }
        return new List<SecurityActionJournalEntry>();
    }

    private static void Save(List<SecurityActionJournalEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(JournalPath)!;
            Directory.CreateDirectory(dir);
            if (!AppPaths.PreserveUnreadable(JournalPath)) return;
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            // #1026: write-to-temp-then-rename so a crash mid-write can't leave partial JSON that
            // Load silently degrades to an empty journal, destroying the whole action history.
            AppPaths.WriteAllTextAtomic(JournalPath, json);
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
        // Only the app can produce this MAC, so an entry forged into the file can't be undone.
        entry.Mac = JournalIntegrityService.Sign(MacDomain, MacFields(entry));
        entry.IsVerified = entry.Mac is not null;
        var updated = new List<SecurityActionJournalEntry>(current) { entry };
        if (updated.Count > MaxEntries) updated = updated.Skip(updated.Count - MaxEntries).ToList();
        Save(updated);
        return updated;
    }

    public static List<SecurityActionJournalEntry> MarkUndone(List<SecurityActionJournalEntry> current, string entryId)
    {
        // Only the verified row is marked (and re-signed, since IsUndone is MAC'd); a forged row
        // reusing the Id is left exactly as it was.
        var updated = current.Select(e => e.Id == entryId && Verify(e) ? CloneUndone(e) : e).ToList();
        Save(updated);
        return updated;
    }

    private static SecurityActionJournalEntry CloneUndone(SecurityActionJournalEntry e)
    {
        var clone = new SecurityActionJournalEntry
        {
            Id = e.Id,
            TimestampUtc = e.TimestampUtc,
            Kind = e.Kind,
            ActionDescription = e.ActionDescription,
            Target = e.Target,
            UndoPayload = e.UndoPayload,
            IsUndone = true,
        };
        clone.Mac = JournalIntegrityService.Sign(MacDomain, MacFields(clone));
        clone.IsVerified = clone.Mac is not null;
        return clone;
    }

    private const string MacDomain = "security-actions";

    /// <summary>True when the entry's MAC proves this app wrote exactly these fields.</summary>
    public static bool Verify(SecurityActionJournalEntry entry) =>
        JournalIntegrityService.Verify(MacDomain, entry.Mac, MacFields(entry));

    // Everything Undo reads (Kind picks the inverse, UndoPayload is its arguments), the text shown
    // beside the Undo button, and IsUndone (CanUndo's gate - MarkUndone re-signs).
    private static string?[] MacFields(SecurityActionJournalEntry e) => new[]
    {
        e.Id, JournalIntegrityService.Flag(e.IsUndone), JournalIntegrityService.Instant(e.TimestampUtc), e.Kind.ToString(), e.ActionDescription, e.Target, e.UndoPayload,
    };

    /// <summary>The undo arguments for an entry, only when its MAC verifies - the one way
    /// SecurityViewModel's Undo gets them, right before it runs the inverse.</summary>
    public static Dictionary<string, string>? TryGetVerifiedUndoPayload(SecurityActionJournalEntry entry, out string? error)
    {
        if (!Verify(entry))
        {
            error = JournalIntegrityService.UnverifiedMessage;
            return null;
        }
        var payload = ParseUndoPayload(entry.UndoPayload);
        error = payload is null ? "This entry has no undo information." : null;
        return payload;
    }

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
        try
        {
            // A null field value would otherwise flow into the undo call as a null argument.
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string?>>(payload);
            return parsed?.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!);
        }
        catch { return null; }
    }
}
