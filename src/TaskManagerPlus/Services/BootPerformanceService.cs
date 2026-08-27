using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Boot time breakdown (#89) and boot-time trend history (#90). Windows records per-boot timing
/// in the Microsoft-Windows-Diagnostics-Performance/Operational event log (event ID 100, "Windows
/// has started up") - the same source Windows' own boot performance troubleshooting tooling reads.
/// The event's exact set of named fields (things like "BootTime", "MainPathBootTime",
/// "BootPostBootTime") is not a documented, versioned schema Microsoft publishes, so rather than
/// hardcode field names this app might get wrong on some Windows build, this reads every
/// millisecond-shaped "Boot...Time..." field the event actually carries and shows them as-is - the
/// largest one found stands in for "total boot time" (sub-phases are necessarily smaller slices of
/// it). This is the same "adaptive, degrade gracefully rather than guess a wrong exact contract"
/// tradeoff EventLogService's own bugcheck-code extraction already documents for a different event.
/// </summary>
public static class BootPerformanceService
{
    private const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";
    private const int BootEventId = 100;

    private static string HistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskManagerPlus", "boot-history.json");

    private const int MaxHistoryEntries = 60;

    /// <summary>Reads the most recent boot-time breakdown, if the event log has one within the
    /// last 30 days. Returns null on any failure (log unavailable, no matching event, access
    /// denied) - the Startup tab should show "not available" rather than an error in that case.</summary>
    public static BootTimeBreakdown? ReadLatest()
    {
        try
        {
            var query = new EventLogQuery(LogName, PathType.LogName,
                $"*[System[(EventID={BootEventId})]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);

            using var record = reader.ReadEvent();
            if (record is null) return null;

            var components = new List<BootTimeComponent>();
            foreach (var (label, ms) in ExtractBootTimeFields(record))
                components.Add(new BootTimeComponent { Label = label, Milliseconds = ms });

            return new BootTimeBreakdown
            {
                BootTime = record.TimeCreated ?? DateTime.Now,
                Components = components.OrderByDescending(c => c.Milliseconds).ToList(),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Adaptive scan of the event's rendered XML for any &lt;Data Name="..."&gt; field
    /// whose name mentions both "Boot" and "Time" and whose value parses as a plausible
    /// millisecond duration (a few seconds to a few minutes) - see the class remarks for why this
    /// doesn't hardcode exact field names.</summary>
    private static IEnumerable<(string Label, int Ms)> ExtractBootTimeFields(EventRecord record)
    {
        string xml;
        try { xml = record.ToXml(); }
        catch { yield break; }

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { yield break; }

        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        foreach (var data in doc.Descendants(ns + "Data"))
        {
            var nameAttr = data.Attribute("Name")?.Value ?? string.Empty;
            if (!nameAttr.Contains("Boot", StringComparison.OrdinalIgnoreCase)) continue;
            if (!nameAttr.Contains("Time", StringComparison.OrdinalIgnoreCase)) continue;

            if (!int.TryParse(data.Value, out int value)) continue;
            if (value < 100 || value > 30 * 60 * 1000) continue; // plausible boot-phase duration only

            yield return (SplitLabel(nameAttr), value);
        }
    }

    /// <summary>"MainPathBootTime" -&gt; "Main Path Boot Time" - a light PascalCase splitter so an
    /// unfamiliar field name still reads reasonably in the UI.</summary>
    private static string SplitLabel(string pascalCase)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < pascalCase.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascalCase[i]) && !char.IsUpper(pascalCase[i - 1]))
                sb.Append(' ');
            sb.Append(pascalCase[i]);
        }
        return sb.ToString();
    }

    /// <summary>#90: appends one sample to the persisted boot-time trend, capped to the most
    /// recent MaxHistoryEntries - a small local log this app builds up itself over time, the same
    /// %AppData%\TaskManagerPlus JSON persistence pattern ThemeService/AlertThresholdsService use.
    /// De-duplicates by boot timestamp so calling this more than once for the same boot (e.g. the
    /// user revisits the Startup tab) doesn't add a repeat entry.</summary>
    public static List<BootHistoryEntry> RecordAndLoadHistory(BootTimeBreakdown? latest)
    {
        var history = LoadHistory();

        if (latest?.TotalMs is { } totalMs && !history.Any(h => h.Timestamp == latest.BootTime))
        {
            history.Add(new BootHistoryEntry { Timestamp = latest.BootTime, TotalMs = totalMs });
            history = history.OrderBy(h => h.Timestamp).TakeLast(MaxHistoryEntries).ToList();
            SaveHistory(history);
        }

        return history;
    }

    private static List<BootHistoryEntry> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var json = File.ReadAllText(HistoryPath);
                var entries = JsonSerializer.Deserialize<List<BootHistoryEntry>>(json);
                if (entries is not null) return entries;
            }
        }
        catch
        {
            // Corrupt/unreadable file - start a fresh history rather than blocking the tab.
        }
        return new List<BootHistoryEntry>();
    }

    private static void SaveHistory(List<BootHistoryEntry> history)
    {
        try
        {
            var dir = Path.GetDirectoryName(HistoryPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(history));
        }
        catch
        {
            // Best-effort - if we can't persist, the trend just won't include this boot.
        }
    }
}
