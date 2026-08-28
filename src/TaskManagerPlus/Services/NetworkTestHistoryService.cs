using System.IO;
using System.Text.Json;

namespace TaskManagerPlus.Services;

/// <summary>One persisted #579/#580/#581 test result row - a flattened, JSON-friendly shape of
/// whichever result record produced it (SpeedTestResult / BufferbloatResult / LanThroughputResult),
/// since the small history list mixes all three test kinds in one chronological feed.</summary>
public sealed class NetworkTestHistoryEntry
{
    public DateTime TimestampUtc { get; set; }
    public string TestKind { get; set; } = string.Empty; // "Download", "Upload", "Bufferbloat", "LAN"
    public string Target { get; set; } = string.Empty;
    public double Mbps { get; set; }
    public string Summary { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
}

/// <summary>
/// Small persisted history for the #579 speed test / #580 bufferbloat grade / #581 LAN throughput
/// test results, so a result doesn't disappear the moment the app restarts. Same plain-JSON-under-
/// AppPaths shape every other settings/history file in this app uses (see NetworkHistoryService's
/// own remarks) - trimmed to the most recent 50 entries rather than NetworkHistoryService's 180-day
/// window, since these are individual on-demand test runs (at most a handful a day) rather than a
/// per-tick sample stream.
/// </summary>
public static class NetworkTestHistoryService
{
    private static string HistoryPath => AppPaths.GetPath("network-test-history.json");
    private const int MaxEntries = 50;

    public static void Add(NetworkTestHistoryEntry entry)
    {
        try
        {
            var entries = Load();
            entries.Insert(0, entry);
            if (entries.Count > MaxEntries) entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            Save(entries);
        }
        catch
        {
            // Best-effort - a failed write shouldn't stop the test result from being shown.
        }
    }

    public static List<NetworkTestHistoryEntry> Load()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var json = File.ReadAllText(HistoryPath);
                var entries = JsonSerializer.Deserialize<List<NetworkTestHistoryEntry>>(json);
                if (entries is not null) return entries;
            }
        }
        catch
        {
            // Corrupt/unreadable file - start fresh rather than blocking on it.
        }
        return new List<NetworkTestHistoryEntry>();
    }

    private static void Save(List<NetworkTestHistoryEntry> entries)
    {
        var dir = Path.GetDirectoryName(HistoryPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
    }
}
