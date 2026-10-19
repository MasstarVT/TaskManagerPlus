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
/// Persisted as traceroute-baselines.json under AppPaths.SettingsDirectory: a missing or corrupt
/// file just means "no baselines saved yet", not a crash - and since the baselines are user-named
/// data, a corrupt file is backed up (AppPaths.PreserveUnreadable) before the next save replaces it.
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

    /// <summary>The named baselines are user-authored, so a file that doesn't parse (or had entries
    /// dropped) is marked for <see cref="AppPaths.PreserveUnreadable"/> - the next Save keeps a .bak
    /// of it instead of silently replacing every other baseline with just the new one.</summary>
    public static TracerouteBaselineFile Load()
    {
        string path = SettingsPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var file = JsonSerializer.Deserialize<TracerouteBaselineFile>(json);
                if (file is null || file.Baselines is null)
                {
                    AppPaths.MarkUnreadable(path);
                    return new TracerouteBaselineFile();
                }

                // SaveBaseline calls Name.Equals on every baseline and Diff walks Hops - explicit
                // nulls from a hand edit are dropped or blanked. Diff's DP table is hops x hops,
                // so a baseline is also capped at 255 hops (the IP TTL ceiling).
                bool dropped = false;
                var kept = file.Baselines.Where(b => b is not null).ToList();
                dropped |= kept.Count != file.Baselines.Count;
                file.Baselines = kept;
                foreach (var b in file.Baselines)
                {
                    b.Name ??= string.Empty;
                    b.Host ??= string.Empty;
                    var hops = b.Hops?.Where(h => h is not null).Take(255).ToList() ?? new List<TracerouteBaselineHop>();
                    dropped |= b.Hops is null || hops.Count != b.Hops.Count;
                    b.Hops = hops;
                }
                if (dropped) AppPaths.MarkUnreadable(path);
                return file;
            }
        }
        catch
        {
            // Corrupt/unreadable file - fall back to "no baselines saved yet", but keep a copy
            // before anything overwrites it.
            AppPaths.MarkUnreadable(path);
        }
        return new TracerouteBaselineFile();
    }

    /// <summary>False when nothing was written - the unreadable original couldn't be backed up
    /// first, or the write itself failed.</summary>
    public static bool Save(TracerouteBaselineFile file)
    {
        string path = SettingsPath;
        try
        {
            if (!AppPaths.PreserveUnreadable(path)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AppPaths.WriteAllTextAtomic(path, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Saves (or overwrites, by name) one baseline from a completed traceroute's parsed
    /// hops. Returns the saved baseline so the caller can immediately diff against it without a
    /// second file read, or null when it couldn't be written.</summary>
    public static TracerouteBaseline? SaveBaseline(string name, string host, IReadOnlyList<TracerouteHop> hops)
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
        return Save(file) ? baseline : null;
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
