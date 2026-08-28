using System.IO;
using System.Text.Json;

namespace TaskManagerPlus.Services;

/// <summary>One probe target's rolling latency baseline (#504).</summary>
public sealed class LatencyBaselineEntry
{
    public string Target { get; set; } = string.Empty; // LatencyTier.ToString()
    public double MedianMs { get; set; }
    public double P95Ms { get; set; }
    public long SampleCount { get; set; }
    public DateTime LastUpdatedUtc { get; set; }
}

public sealed class LatencyBaselineFile
{
    /// <summary>#504: "a configurable multiple" - how many times over baseline counts as worth
    /// flagging. User-adjustable from the Network tab; persisted here alongside the baselines it
    /// judges against.</summary>
    public double DeviationMultiplier { get; set; } = 3.0;
    public List<LatencyBaselineEntry> Entries { get; set; } = new();
}

/// <summary>
/// Item #504: persists a rolling median/p95 latency baseline per probe target to
/// latency-baseline.json under AppPaths.SettingsDirectory, so the Latency card can flag "gateway
/// latency is 6x your usual 2 ms" instead of just showing a bare number with no sense of whether
/// it's unusual. Distinct from #506's day-by-day latency-history.json: that file exists to answer
/// "what did last night look like", this one exists purely to answer "is right now unusual".
/// Same fail-silent-to-defaults JSON pattern as theme.json/ThemeService - a missing or corrupt
/// file just means "no baseline yet", not a crash.
/// </summary>
public static class LatencyBaselineService
{
    private static string SettingsPath => AppPaths.GetPath("latency-baseline.json");

    // The baseline is a slow-moving figure, not a strict recompute-from-scratch each call - every
    // update blends the new window's median/p95 into the stored baseline at this weight, so one
    // unusually busy window can't instantly redefine "usual", but weeks of genuinely different
    // conditions (new router, new ISP) eventually do shift it.
    private const double BlendWeight = 0.08;
    private const int MinSamplesToUpdate = 20;

    public static LatencyBaselineFile Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var file = JsonSerializer.Deserialize<LatencyBaselineFile>(json);
                if (file is not null) return file;
            }
        }
        catch
        {
            // Corrupt/unreadable settings file - fall back to defaults.
        }
        return new LatencyBaselineFile();
    }

    public static void Save(LatencyBaselineFile file)
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

    /// <summary>Blends the current window's successful round-trips into the persisted baseline
    /// for one target, bootstrapping it directly the first time a target is seen. No-ops below
    /// <see cref="MinSamplesToUpdate"/> successful samples in the window - too few to trust as
    /// "usual" yet. Mutates <paramref name="file"/> in place; caller is responsible for
    /// Save()-ing it (NetworkViewModel does this on the same periodic cadence #506 flushes
    /// history on, so the two files stay roughly in step without a second timer).</summary>
    public static void UpdateBaseline(LatencyBaselineFile file, string target, IReadOnlyList<double> successfulRoundtripsMs)
    {
        if (successfulRoundtripsMs.Count < MinSamplesToUpdate) return;

        double median = Percentile(successfulRoundtripsMs, 0.5);
        double p95 = Percentile(successfulRoundtripsMs, 0.95);

        var entry = file.Entries.FirstOrDefault(e => e.Target == target);
        if (entry is null)
        {
            file.Entries.Add(new LatencyBaselineEntry
            {
                Target = target,
                MedianMs = median,
                P95Ms = p95,
                SampleCount = successfulRoundtripsMs.Count,
                LastUpdatedUtc = DateTime.UtcNow,
            });
            return;
        }

        entry.MedianMs = entry.MedianMs <= 0 ? median : entry.MedianMs * (1 - BlendWeight) + median * BlendWeight;
        entry.P95Ms = entry.P95Ms <= 0 ? p95 : entry.P95Ms * (1 - BlendWeight) + p95 * BlendWeight;
        entry.SampleCount += successfulRoundtripsMs.Count;
        entry.LastUpdatedUtc = DateTime.UtcNow;
    }

    /// <summary>"Gateway latency is 6.2x your usual 2 ms" style message, or null when there's no
    /// baseline yet, the baseline is too close to zero to divide by meaningfully, or the current
    /// reading isn't over the configured multiple. Quick flag, not a verdict - a single busy
    /// window crossing the line doesn't mean anything is actually wrong, just that it's worth a
    /// look.</summary>
    public static string? GetDeviationMessage(LatencyBaselineFile file, string target, string label, double currentAvgMs)
    {
        var entry = file.Entries.FirstOrDefault(e => e.Target == target);
        if (entry is null || entry.MedianMs <= 0.5 || currentAvgMs <= 0) return null;

        double ratio = currentAvgMs / entry.MedianMs;
        if (ratio < file.DeviationMultiplier) return null;

        return $"{label} latency is {ratio:0.#}x your usual {entry.MedianMs:0.#} ms — quick flag, not a verdict.";
    }

    private static double Percentile(IReadOnlyList<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];

        double rank = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
