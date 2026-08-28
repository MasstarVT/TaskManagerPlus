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
/// </summary>
public static class AlertHistoryService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { Converters = { new JsonStringEnumConverter() } };

    private static string LogPath => AppPaths.GetPath("alerts-history.jsonl");

    public static void Append(AlertHistoryEntry entry)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.AppendAllText(LogPath, JsonSerializer.Serialize(entry, JsonOpts) + Environment.NewLine);
        }
        catch
        {
            // Best-effort - a failed history append never blocks alert delivery.
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
            foreach (var line in File.ReadLines(LogPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<AlertHistoryEntry>(line, JsonOpts);
                    if (entry is not null && entry.TimestampUtc >= cutoff) result.Add(entry);
                }
                catch
                {
                    // Skip one malformed line rather than losing the rest of the history.
                }
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
            foreach (var line in File.ReadLines(LogPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<AlertHistoryEntry>(line, JsonOpts);
                    if (entry is not null && entry.TimestampUtc >= cutoff &&
                        string.Equals(entry.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))
                        count++;
                }
                catch
                {
                    // Skip one malformed line.
                }
            }
        }
        catch
        {
            // Best-effort - a failed read just means escalation doesn't trigger this pass.
        }
        return count;
    }
}
