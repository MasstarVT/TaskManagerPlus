using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #936: append-only findings-history.jsonl under AppPaths.SettingsDirectory - one JSON object per
/// line, one line per finding state transition (SummaryViewModel.RefreshHealthIssues diffs the
/// previous pass's fired-rule-id set against the current one and calls Append on an edge). Kept
/// deliberately separate from RulesEngineService.JsonOpts (WriteIndented would break the
/// one-object-per-line format this file needs). Best-effort like every other settings/log file in
/// this app - a failed read/write never blocks or crashes the Health Check card.
/// </summary>
public static class FindingsHistoryService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { Converters = { new JsonStringEnumConverter() } };

    private static string LogPath => AppPaths.GetPath("findings-history.jsonl");

    public static void Append(FindingsHistoryEntry entry)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.AppendAllText(LogPath, JsonSerializer.Serialize(entry, JsonOpts) + Environment.NewLine);
        }
        catch
        {
            // Best-effort - a failed history append never blocks the Health Check card's refresh.
        }
    }

    /// <summary>SummaryViewModel's initial "Recently resolved" state - the most recent `maxCount`
    /// "resolved" transitions from prior sessions, newest first. A malformed line is skipped
    /// rather than aborting the whole read (the file is appended to by this app only, but a
    /// crash mid-write could still leave a partial last line).</summary>
    public static List<ResolvedFinding> LoadRecentResolved(int maxCount)
    {
        var result = new List<ResolvedFinding>();
        try
        {
            if (!File.Exists(LogPath)) return result;

            foreach (var line in File.ReadLines(LogPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<FindingsHistoryEntry>(line, JsonOpts);
                    if (entry is { Transition: "resolved" })
                        result.Add(new ResolvedFinding { RuleId = entry.RuleId, Title = entry.Title, ResolvedAtUtc = entry.TimestampUtc });
                }
                catch
                {
                    // Skip one malformed line rather than losing the rest of the history.
                }
            }
        }
        catch
        {
            // Best-effort - a failed read just means "Recently resolved" starts empty this session.
        }

        return result.OrderByDescending(r => r.ResolvedAtUtc).Take(maxCount).ToList();
    }
}
