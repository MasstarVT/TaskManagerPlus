using System.IO;
using System.Text.Json;

namespace TaskManagerPlus.Services;

/// <summary>One saved hop of a baseline (#516) - a plain settable-property class (not
/// TracerouteHop itself) so it round-trips through System.Text.Json the same predictable way
/// every other persisted settings class in this app does (LatencyBaselineEntry, NetworkHistoryEntry, ...).</summary>
public sealed class TracerouteBaselineHop
{
    public int HopNumber { get; set; }
    public string? Ip { get; set; }
}

public sealed class TracerouteBaseline
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public DateTime SavedUtc { get; set; }
    public List<TracerouteBaselineHop> Hops { get; set; } = new();
}

public sealed class TracerouteBaselineFile
{
    public List<TracerouteBaseline> Baselines { get; set; } = new();
}

/// <summary>Kind of change one diffed hop represents (#516).</summary>
public enum TracerouteDiffKind { Unchanged, Reordered, Inserted, Removed }

/// <summary>One row of a baseline-vs-current diff (#516). <see cref="BaselineHop"/>/<see
/// cref="CurrentHop"/> are null when the row only exists on one side (Inserted/Removed).</summary>
public sealed record TracerouteDiffEntry(int? BaselineHop, int? CurrentHop, string Ip, TracerouteDiffKind Kind, string Note);

/// <summary>
/// Item #516: saves a traceroute result as a named baseline and diffs a later run against it,
/// turning the existing one-shot text dump into an actionable "your ISP rerouted you through a
/// different city" signal instead of two blocks of text the user has to eyeball themselves.
///
/// Persisted as traceroute-baselines.json under AppPaths.SettingsDirectory - same fail-silent-to-
/// defaults JSON pattern as latency-baseline.json/theme.json (LatencyBaselineService/ThemeService):
/// a missing or corrupt file just means "no baselines saved yet", not a crash.
///
/// The diff itself is a classic LCS (longest common subsequence) alignment over hop IP addresses -
/// a hop present in both sequences at the same relative order counts as Unchanged (or Reordered if
/// its position shifted), a hop only in the baseline is Removed, a hop only in the current run is
/// Inserted. A "Request timed out" hop (null IP) never counts as a match against anything, even
/// another timed-out hop at the same position - two blank hops don't prove the router at that
/// position is actually the same one.
/// </summary>
public static class TracerouteBaselineService
{
    private static string SettingsPath => AppPaths.GetPath("traceroute-baselines.json");

    public static TracerouteBaselineFile Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var file = JsonSerializer.Deserialize<TracerouteBaselineFile>(json);
                if (file is not null) return file;
            }
        }
        catch
        {
            // Corrupt/unreadable file - fall back to "no baselines saved yet".
        }
        return new TracerouteBaselineFile();
    }

    public static void Save(TracerouteBaselineFile file)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - if we can't persist, the app still works for this session.
        }
    }

    /// <summary>Saves (or overwrites, by name) one baseline from a completed traceroute's parsed
    /// hops. Returns the saved baseline so the caller can immediately diff against it without a
    /// second file read.</summary>
    public static TracerouteBaseline SaveBaseline(string name, string host, IReadOnlyList<TracerouteHop> hops)
    {
        var file = Load();
        var baseline = new TracerouteBaseline
        {
            Name = name,
            Host = host,
            SavedUtc = DateTime.UtcNow,
            Hops = hops.Select(h => new TracerouteBaselineHop { HopNumber = h.HopNumber, Ip = h.Ip }).ToList(),
        };

        file.Baselines.RemoveAll(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        file.Baselines.Add(baseline);
        Save(file);
        return baseline;
    }

    /// <summary>LCS-based diff between a saved baseline and a freshly-run traceroute's hops - see
    /// this class's remarks for the alignment rules.</summary>
    public static List<TracerouteDiffEntry> Diff(TracerouteBaseline baseline, IReadOnlyList<TracerouteHop> current)
    {
        var baseHops = baseline.Hops;
        int n = baseHops.Count, m = current.Count;

        // dp[i, j] = length of the longest common subsequence of baseHops[i..] and current[j..].
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                bool matches = baseHops[i].Ip is not null && baseHops[i].Ip == current[j].Ip;
                dp[i, j] = matches ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var entries = new List<TracerouteDiffEntry>();
        int a = 0, b = 0;
        while (a < n || b < m)
        {
            bool canMatch = a < n && b < m && baseHops[a].Ip is not null && baseHops[a].Ip == current[b].Ip;
            if (canMatch)
            {
                bool reordered = a != b;
                entries.Add(new TracerouteDiffEntry(
                    baseHops[a].HopNumber, current[b].HopNumber, baseHops[a].Ip!,
                    reordered ? TracerouteDiffKind.Reordered : TracerouteDiffKind.Unchanged,
                    reordered ? $"Now hop {current[b].HopNumber}, was hop {baseHops[a].HopNumber}" : "Unchanged"));
                a++; b++;
            }
            else if (b < m && (a >= n || dp[a, b + 1] >= dp[a + 1, b]))
            {
                entries.Add(new TracerouteDiffEntry(
                    null, current[b].HopNumber, current[b].Ip ?? "(no reply)", TracerouteDiffKind.Inserted,
                    "New hop, not present in the baseline"));
                b++;
            }
            else
            {
                entries.Add(new TracerouteDiffEntry(
                    baseHops[a].HopNumber, null, baseHops[a].Ip ?? "(no reply)", TracerouteDiffKind.Removed,
                    "Present in the baseline, missing now"));
                a++;
            }
        }
        return entries;
    }
}
