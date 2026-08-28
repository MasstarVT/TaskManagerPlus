using System.IO;
using System.Text.Json;

namespace TaskManagerPlus.Services;

/// <summary>One day's accumulated connection-count samples for one process, plus (#583) whatever
/// real byte totals any #582 ETW capture has contributed for that same process/day.</summary>
public sealed class NetworkHistoryEntry
{
    public string Date { get; set; } = string.Empty; // yyyy-MM-dd, local time
    public string ProcessName { get; set; } = string.Empty;
    public long ConnectionCountTotal { get; set; }
    public int SampleCount { get; set; }

    // #583: a second, clearly distinguished byte-level series - only ever non-zero for a
    // process/day where the user actually ran a #582 ETW capture that saw that process. Zero
    // here means "no capture data", not "measured zero bytes" - see HasByteData below and
    // NetworkViewModel's remarks for how the view distinguishes the two.
    public long BytesSentTotal { get; set; }
    public long BytesReceivedTotal { get; set; }
    public int CaptureCount { get; set; }

    /// <summary>True once at least one #582 capture has actually contributed data for this
    /// row - lets the History Today/This Month lists show a real data-volume column instead of
    /// (or alongside) the connection-count proxy once captures have been run, without confusing
    /// "never captured" with "captured zero bytes".</summary>
    public bool HasByteData => CaptureCount > 0;
}

/// <summary>
/// Historical bandwidth-by-app totals (round 9, #45) - persisted daily per process name, distinct
/// from NetworkConnectionsService's existing *live* connection-count proxy. This is deliberately
/// NOT byte-level bandwidth history: Round 6's Network section already documents, for the live
/// proxy, that Windows exposes no public API for true per-process byte attribution (Task
/// Manager's own network column is built on an undocumented NSI call this app has consistently
/// avoided taking a dependency on). A historical figure built from an unmeasurable quantity would
/// just be a persisted fabrication, so this instead persists the SAME honest connection-count
/// proxy, aggregated per day/month - "which process held the most simultaneous connections,
/// summed across today's samples" rather than "which process used the most data". Stored as plain
/// JSON under %AppData%\TaskManagerPlus\network-history.json, the same persistence shape as
/// boot-history.json/alerts.json, trimmed to the most recent 180 days so the file can't grow
/// unbounded on a long-lived install.
/// </summary>
public static class NetworkHistoryService
{
    // Round 12, #87: routed through AppPaths so portable mode redirects this next to the exe.
    private static string HistoryPath => AppPaths.GetPath("network-history.json");

    private const int MaxDays = 180;

    /// <summary>Adds one sample's connection counts to today's running per-process totals and
    /// persists the result. Called once per NetworkViewModel connectivity-check tick (the existing
    /// 15s timer, per this round's established "reuse an existing timer rather than add a new
    /// poller" precedent) - each call is a full read-modify-write of a small JSON file, acceptable
    /// at that cadence.</summary>
    public static void RecordSample(IEnumerable<NetworkProcessUsage> usages)
    {
        try
        {
            var entries = Load();
            string today = DateTime.Now.ToString("yyyy-MM-dd");

            foreach (var usage in usages)
            {
                var existing = entries.FirstOrDefault(e => e.Date == today &&
                    e.ProcessName.Equals(usage.ProcessName, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    existing = new NetworkHistoryEntry { Date = today, ProcessName = usage.ProcessName };
                    entries.Add(existing);
                }
                existing.ConnectionCountTotal += usage.ConnectionCount;
                existing.SampleCount++;
            }

            var cutoff = DateTime.Now.AddDays(-MaxDays);
            entries = entries.Where(e => DateTime.TryParse(e.Date, out var d) && d >= cutoff).ToList();

            Save(entries);
        }
        catch
        {
            // Best-effort - a failed write shouldn't disrupt the connectivity-check tick it rides on.
        }
    }

    /// <summary>#583: adds one completed #582 ETW capture's per-process byte totals to today's
    /// running totals and persists the result - the same read-modify-write shape RecordSample
    /// above already uses, called once when the user clicks "Stop" on a capture (never on a
    /// timer - a capture is explicitly user-initiated, per CLAUDE.md's on-demand convention).</summary>
    public static void RecordCaptureSample(IEnumerable<EtwProcessBandwidth> capture)
    {
        try
        {
            var entries = Load();
            string today = DateTime.Now.ToString("yyyy-MM-dd");

            foreach (var proc in capture)
            {
                var existing = entries.FirstOrDefault(e => e.Date == today &&
                    e.ProcessName.Equals(proc.ProcessName, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    existing = new NetworkHistoryEntry { Date = today, ProcessName = proc.ProcessName };
                    entries.Add(existing);
                }
                existing.BytesSentTotal += proc.BytesSent;
                existing.BytesReceivedTotal += proc.BytesReceived;
                existing.CaptureCount++;
            }

            var cutoff = DateTime.Now.AddDays(-MaxDays);
            entries = entries.Where(e => DateTime.TryParse(e.Date, out var d) && d >= cutoff).ToList();

            Save(entries);
        }
        catch
        {
            // Best-effort - a failed write shouldn't stop the capture result from being shown live.
        }
    }

    /// <summary>Per-process totals for one calendar day (default: today) - sorted by whichever
    /// series has real data (#583's byte totals when any capture has run that day, else the #45
    /// connection-count proxy).</summary>
    public static List<NetworkHistoryEntry> GetDayTotals(DateTime? day = null)
    {
        string target = (day ?? DateTime.Now).ToString("yyyy-MM-dd");
        return Load().Where(e => e.Date == target)
            .OrderByDescending(e => e.HasByteData)
            .ThenByDescending(e => e.HasByteData ? e.BytesSentTotal + e.BytesReceivedTotal : e.ConnectionCountTotal)
            .ToList();
    }

    /// <summary>Per-process totals summed across the current calendar month, sorted the same way
    /// GetDayTotals is.</summary>
    public static List<NetworkHistoryEntry> GetMonthTotals()
    {
        string prefix = DateTime.Now.ToString("yyyy-MM");
        return Load()
            .Where(e => e.Date.StartsWith(prefix, StringComparison.Ordinal))
            .GroupBy(e => e.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new NetworkHistoryEntry
            {
                Date = prefix,
                ProcessName = g.Key,
                ConnectionCountTotal = g.Sum(e => e.ConnectionCountTotal),
                SampleCount = g.Sum(e => e.SampleCount),
                BytesSentTotal = g.Sum(e => e.BytesSentTotal),
                BytesReceivedTotal = g.Sum(e => e.BytesReceivedTotal),
                CaptureCount = g.Sum(e => e.CaptureCount),
            })
            .OrderByDescending(e => e.HasByteData)
            .ThenByDescending(e => e.HasByteData ? e.BytesSentTotal + e.BytesReceivedTotal : e.ConnectionCountTotal)
            .ToList();
    }

    private static List<NetworkHistoryEntry> Load()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var json = File.ReadAllText(HistoryPath);
                var entries = JsonSerializer.Deserialize<List<NetworkHistoryEntry>>(json);
                if (entries is not null) return entries;
            }
        }
        catch
        {
            // Corrupt/unreadable file - start fresh rather than blocking on it.
        }
        return new List<NetworkHistoryEntry>();
    }

    private static void Save(List<NetworkHistoryEntry> entries)
    {
        var dir = Path.GetDirectoryName(HistoryPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }
}
