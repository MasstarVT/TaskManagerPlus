using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #693: persisted AC-vs-battery steady-state sample history (ac-dc-cliff-samples.json) - measured,
/// not configured, the way #662's plan-setting diff is. A laptop's own AC and battery sustained-load
/// sessions are often days or weeks apart, so this needs to survive an app restart the same way
/// throttle-history.json/power-history-log.json do; same load/append/cap-and-save JSON shape as
/// every other persisted-history service in this app (GpuHangHistoryService et al.), fails silently
/// to "no history" on a missing or corrupt file.
/// </summary>
public static class AcDcCliffService
{
    // A steady-state sample is only captured a few times a minute at most (see
    // EnergyThermalsViewModel's sustained-load gate) - 300 entries is easily months of both-sides
    // history without growing into a real telemetry log.
    private const int MaxSamples = 300;

    /// <summary>A side (AC or DC) needs at least this many samples before its average is shown as a
    /// confident figure - one stray sample right after plugging/unplugging shouldn't produce a
    /// misleadingly precise-looking number.</summary>
    public const int MinSamplesForSummary = 5;

    private static string SettingsPath => AppPaths.GetPath("ac-dc-cliff-samples.json");

    public static List<AcDcSteadyStateSample> Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var list = JsonSerializer.Deserialize<List<AcDcSteadyStateSample>>(json);
                if (list is not null) return list;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - fall back to "no history".
        }
        return new List<AcDcSteadyStateSample>();
    }

    public static List<AcDcSteadyStateSample> Append(List<AcDcSteadyStateSample> existing, AcDcSteadyStateSample sample)
    {
        existing.Add(sample);
        if (existing.Count > MaxSamples) existing.RemoveRange(0, existing.Count - MaxSamples);

        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort - if we can't persist, this one sample just won't survive a restart.
        }

        return existing;
    }

    /// <summary>Reduces the sample history to one AC-side and one DC-side average - the "measured
    /// sustained-performance cliff" #693 reports. HasAcData/HasDcData gate on MinSamplesForSummary
    /// so the card doesn't show a confident-looking number from a single sample.</summary>
    public static AcDcCliffSummary Summarize(IReadOnlyList<AcDcSteadyStateSample> samples)
    {
        var ac = samples.Where(s => !s.OnBattery).ToList();
        var dc = samples.Where(s => s.OnBattery).ToList();

        return new AcDcCliffSummary
        {
            HasAcData = ac.Count >= MinSamplesForSummary,
            HasDcData = dc.Count >= MinSamplesForSummary,
            AcClockGhz = ac.Count > 0 ? ac.Average(s => s.ClockGhz) : 0,
            DcClockGhz = dc.Count > 0 ? dc.Average(s => s.ClockGhz) : 0,
            AcPackagePowerW = ac.Count > 0 ? ac.Average(s => s.PackagePowerW) : 0,
            DcPackagePowerW = dc.Count > 0 ? dc.Average(s => s.PackagePowerW) : 0,
            AcTempC = ac.Count > 0 ? ac.Average(s => s.TempC) : 0,
            DcTempC = dc.Count > 0 ? dc.Average(s => s.TempC) : 0,
            AcSampleCount = ac.Count,
            DcSampleCount = dc.Count,
        };
    }
}
