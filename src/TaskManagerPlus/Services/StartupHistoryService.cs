using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #748: persists each scan's measured startup delay per item, keyed by item Name, to
/// startup-history.json under AppPaths.SettingsDirectory - same small-JSON/fail-silently-to-
/// defaults settings pattern as boot-history.json (BootPerformanceService). Written once per scan
/// (whenever StartupViewModel.Refresh's #91 delay scan measures a real delay for a still-running
/// item), not on any timer - the Startup tab itself is on-demand (initial load + manual Refresh),
/// so this rides that same cadence rather than adding a new one.
///
/// Shows a median plus a small sparkline per row instead of one volatile single-scan number, plus
/// a "grown from Xs to Ys over your last N boots" flag when the trend looks like sustained growth
/// rather than scan-to-scan noise - a quick flag, not a verdict (see CLAUDE.md's cross-cutting
/// conventions): a one-off slow scan (something else was busy on the machine right then) can look
/// like growth for a sample or two without actually being one.
/// </summary>
public static class StartupHistoryService
{
    private static string HistoryPath => AppPaths.GetPath("startup-history.json");
    private const int MaxSamplesPerItem = 20;

    // Fixed logical size for the sparkline's coordinate space - the Polyline this renders into is
    // stretched to whatever size the grid cell actually is, so these are just the numbers the
    // "x,y x,y ..." point string is expressed in, not literal pixels.
    private const double SparkWidth = 60, SparkHeight = 18;

    public static Dictionary<string, List<StartupCostSample>> Load()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var json = File.ReadAllText(HistoryPath);
                var data = JsonSerializer.Deserialize<Dictionary<string, List<StartupCostSample>>>(json);
                if (data is not null) return new Dictionary<string, List<StartupCostSample>>(data, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Corrupt/unreadable file - start a fresh history rather than blocking the tab.
        }
        return new Dictionary<string, List<StartupCostSample>>(StringComparer.OrdinalIgnoreCase);
    }

    private static void Save(Dictionary<string, List<StartupCostSample>> data)
    {
        try
        {
            var dir = Path.GetDirectoryName(HistoryPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(data));
        }
        catch
        {
            // Best-effort - if this can't persist, the trend just won't include this scan.
        }
    }

    /// <summary>Appends this scan's measured delay (when present) for each named item, saves once,
    /// and returns the per-item stats (median/sparkline/growth flag) the grid shows - keyed by the
    /// same item Name the caller passed in, so it maps straight back onto StartupItem rows.</summary>
    public static Dictionary<string, StartupCostStats> RecordAndCompute(IEnumerable<(string Name, double? DelaySeconds)> measurements)
    {
        var data = Load();
        bool changed = false;

        foreach (var (name, delaySeconds) in measurements)
        {
            if (name.Length == 0) continue;
            if (delaySeconds is not { } seconds) continue; // "not currently running" this scan - nothing to record

            if (!data.TryGetValue(name, out var samples))
            {
                samples = new List<StartupCostSample>();
                data[name] = samples;
            }
            samples.Add(new StartupCostSample { RecordedAtUtc = DateTime.UtcNow, DelaySeconds = seconds });
            if (samples.Count > MaxSamplesPerItem) samples.RemoveRange(0, samples.Count - MaxSamplesPerItem);
            changed = true;
        }

        if (changed) Save(data);

        var result = new Dictionary<string, StartupCostStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, samples) in data)
        {
            if (samples.Count == 0) continue;
            result[name] = BuildStats(samples);
        }
        return result;
    }

    private static StartupCostStats BuildStats(List<StartupCostSample> samples)
    {
        var values = samples.Select(s => s.DelaySeconds).ToList();
        double median = Median(values);

        string? trendFlag = null;
        if (samples.Count >= 5)
        {
            // Compares the average of the first third of the retained samples against the average
            // of the last third, so one noisy single sample at either end can't flip the flag on
            // its own - a coarse smoothing, not a statistical trend test.
            int third = Math.Max(1, samples.Count / 3);
            double early = values.Take(third).Average();
            double recent = values.TakeLast(third).Average();
            if (early > 0.05 && recent >= early * 2.0 && recent - early >= 1.0)
                trendFlag = $"Grown from {early:0.#}s to {recent:0.#}s over your last {samples.Count} boots.";
        }

        return new StartupCostStats
        {
            MedianDelaySeconds = median,
            SampleCount = values.Count,
            SparklinePointsText = BuildSparkline(values),
            TrendFlag = trendFlag,
        };
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int n = sorted.Count;
        if (n == 0) return 0;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    /// <summary>Renders the retained samples (oldest first) as a "x,y x,y ..." point string for a
    /// Polyline bound directly via WPF's built-in PointCollectionConverter - no value converter
    /// needed, the same "bind a mini-language string straight to the property" trick this app's
    /// XAML already leans on for Geometry/Data bindings. Normalized to this row's own min/max, not
    /// a shared scale across rows - a sparkline's job is to show shape, not an absolute comparison.</summary>
    private static string BuildSparkline(List<double> values)
    {
        if (values.Count < 2) return string.Empty;

        double min = values.Min(), max = values.Max();
        double range = max - min;
        var points = new List<string>(values.Count);
        for (int i = 0; i < values.Count; i++)
        {
            double x = i * SparkWidth / (values.Count - 1);
            double norm = range < 0.001 ? 0.5 : (values[i] - min) / range;
            double y = SparkHeight - norm * SparkHeight;
            points.Add($"{x.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)},{y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        return string.Join(" ", points);
    }
}
