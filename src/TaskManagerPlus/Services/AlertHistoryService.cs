using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #963: append-only alerts-history.jsonl under AppPaths.SettingsDirectory - one JSON object per
/// line, one line per alert actually raised (the three fixed thresholds from
/// SummaryViewModel.CheckThresholdAlerts, plus every rule-engine finding that newly fires - see
/// AlertDeliveryService, the one place that appends here). Deliberately its own file rather than
/// reusing findings-history.jsonl: that file tracks finding state *transitions* for the Health
/// Check card's own UI, this one is specifically the alert/notification record #963-#965's digest
/// and escalation counting need. Best-effort like every other log file in this app.
///
/// Pruned on append once it passes <see cref="PruneThresholdBytes"/>: every read (the escalation
/// count on each delivered alert, the Background Health digest) parses the whole file, and an
/// unpruned file grew without bound.
/// </summary>
public static class AlertHistoryService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { Converters = { new JsonStringEnumConverter() } };

    private static string LogPath => AppPaths.GetPath("alerts-history.jsonl");

    // Lines are ~250 bytes, so 1 MB is roughly 4,000 alerts; a prune keeps at most MaxKeptLines
    // from the last MaxAge, which covers the 7-day digest and any realistic escalation window.
    private const long PruneThresholdBytes = 1024 * 1024;
    private const int MaxKeptLines = 2000;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    // Append and prune both rewrite the file; the digest reads off the UI thread.
    private static readonly object WriteLock = new();

    public static void Append(AlertHistoryEntry entry)
    {
        lock (WriteLock)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.SettingsDirectory);
                File.AppendAllText(LogPath, JsonSerializer.Serialize(entry, JsonOpts) + Environment.NewLine);
            }
            catch
            {
                // Best-effort - a failed history append never blocks alert delivery.
                return;
            }

            try { PruneIfLarge(); }
            catch { /* best-effort - an oversized file is still readable, just slower */ }
        }
    }

    private static void PruneIfLarge()
    {
        var info = new FileInfo(LogPath);
        if (!info.Exists || info.Length < PruneThresholdBytes) return;

        var cutoff = DateTime.UtcNow - MaxAge;
        var kept = new List<string>();
        foreach (var line in ReadSharedLines(LogPath))
        {
            if (TryParse(line) is { } e && e.TimestampUtc >= cutoff) kept.Add(line);
        }
        if (kept.Count > MaxKeptLines) kept = kept.GetRange(kept.Count - MaxKeptLines, MaxKeptLines);

        // Temp file + rename: an interrupted prune leaves the old file, never a truncated one.
        string tmp = LogPath + ".tmp";
        File.WriteAllLines(tmp, kept);
        File.Move(tmp, LogPath, overwrite: true);
    }

    /// <summary>Doesn't lock an append out (File.ReadLines shares Read only, so a concurrent
    /// AppendAllText would fail and silently drop the alert's line).</summary>
    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            yield return line;
    }

    /// <summary>One line, or null for a blank/malformed one. Explicit JSON nulls overwrite the
    /// model's initializers, so the string fields are re-blanked for the digest's grouping and
    /// the panel's bindings.</summary>
    private static AlertHistoryEntry? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<AlertHistoryEntry>(line, JsonOpts);
            if (entry is null) return null;
            entry.RuleId ??= string.Empty;
            entry.Title ??= string.Empty;
            entry.Message ??= string.Empty;
            return entry;
        }
        catch
        {
            return null; // skip one malformed line rather than losing the rest of the history
        }
    }

    /// <summary>#963's digest / the Background Health panel's "recent alerts" list - every entry
    /// within the trailing `window`, newest first. A malformed line is skipped rather than
    /// aborting the whole read.</summary>
    public static List<AlertHistoryEntry> LoadRecent(TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        var result = new List<AlertHistoryEntry>();
        try
        {
            if (!File.Exists(LogPath)) return result;
            foreach (var line in ReadSharedLines(LogPath))
            {
                if (TryParse(line) is { } entry && entry.TimestampUtc >= cutoff) result.Add(entry);
            }
        }
        catch
        {
            // Best-effort - a failed read just means the digest starts empty this pass.
        }
        return result.OrderByDescending(e => e.TimestampUtc).ToList();
    }

    /// <summary>#965: how many times `ruleId` fired within the trailing `window` - used by
    /// AlertDeliveryService to decide whether this alert should escalate. Counts every prior
    /// entry regardless of channel/suppression, since escalation is about repetition of the
    /// underlying condition, not about how loudly past instances were shown.</summary>
    public static int CountRecent(string ruleId, TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        int count = 0;
        try
        {
            if (!File.Exists(LogPath)) return 0;
            foreach (var line in ReadSharedLines(LogPath))
            {
                if (TryParse(line) is { } entry && entry.TimestampUtc >= cutoff &&
                    string.Equals(entry.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))
                    count++;
            }
        }
        catch
        {
            // Best-effort - a failed read just means escalation doesn't trigger this pass.
        }
        return count;
    }
}
