using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #972: append-only JSONL log of every system mutation this app has performed - one line per
/// ChangeJournalEntry, newest entries appended at the end. Static, like every other small settings/
/// log service in this app (PollIntervalSettingsService, FindingsHistoryService, ...) - no
/// in-memory cache, since it's read rarely (only when the Troubleshoot tab's "Changes made by this
/// app" panel is opened) and written from several different ViewModels that don't otherwise share
/// state.
/// </summary>
public static class ChangeJournalService
{
    private static string JournalPath => AppPaths.GetPath("change-journal.jsonl");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Appends one entry - best-effort, fails silently (a missing journal line is
    /// unfortunate but must never be the reason the mutation itself, or the rest of the app,
    /// fails).</summary>
    public static void Append(ChangeJournalEntry entry)
    {
        try
        {
            var dir = Path.GetDirectoryName(JournalPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(JournalPath, JsonSerializer.Serialize(entry, JsonOpts) + Environment.NewLine);
        }
        catch
        {
            // Best-effort - see remarks above.
        }
    }

    /// <summary>Newest-first, per #973's "Changes made by this app" panel. Corrupt/unreadable
    /// lines are skipped individually rather than losing the whole journal to one bad line.</summary>
    public static List<ChangeJournalEntry> LoadAll()
    {
        var result = new List<ChangeJournalEntry>();
        try
        {
            if (!File.Exists(JournalPath)) return result;

            foreach (var line in File.ReadAllLines(JournalPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<ChangeJournalEntry>(line, JsonOpts);
                    if (entry is not null) result.Add(entry);
                }
                catch
                {
                    // One malformed line - skip it, keep the rest.
                }
            }
        }
        catch
        {
            // Unreadable file - degrade to an empty journal rather than throw.
        }

        result.Reverse(); // newest first
        return result;
    }

    /// <summary>#973: flips Undone/UndoneAtUtc on the matching entry once its inverse operation has
    /// actually run successfully. JSONL has no in-place edit, so this rewrites the whole file - fine
    /// at this journal's expected size (a handful to a few hundred entries for a single machine's
    /// lifetime of use through this app).</summary>
    public static void MarkUndone(string entryId)
    {
        try
        {
            if (!File.Exists(JournalPath)) return;

            var lines = File.ReadAllLines(JournalPath);
            var rewritten = new List<string>(lines.Length);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) { rewritten.Add(line); continue; }
                try
                {
                    var entry = JsonSerializer.Deserialize<ChangeJournalEntry>(line, JsonOpts);
                    if (entry is not null && string.Equals(entry.Id, entryId, StringComparison.Ordinal))
                    {
                        entry.Undone = true;
                        entry.UndoneAtUtc = DateTime.UtcNow;
                        rewritten.Add(JsonSerializer.Serialize(entry, JsonOpts));
                        continue;
                    }
                }
                catch { /* leave the line as-is */ }
                rewritten.Add(line);
            }
            File.WriteAllLines(JournalPath, rewritten);
        }
        catch
        {
            // Best-effort - see Append's remarks.
        }
    }
}
