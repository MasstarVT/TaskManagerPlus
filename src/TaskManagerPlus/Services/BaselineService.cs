using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Services;

/// <summary>
/// #950-957: captures, saves, loads, and compares <see cref="PerformanceBaseline"/>s under
/// AppPaths.SettingsDirectory\Baselines\ - the heavier, opt-in sibling of SnapshotService's
/// existing lightweight ad-hoc snapshot files (see PerformanceBaseline's remarks for why it wraps
/// rather than duplicates SystemSnapshot). Static/stateless, same shape as SnapshotService - all
/// per-capture state (the idle tracker, in-flight capture status) lives on BaselineViewModel, not
/// here.
/// </summary>
public static class BaselineService
{
    private static string BaselinesDirectory => AppPaths.GetPath("Baselines");

    /// <summary>
    /// #950: orchestrates one full baseline capture - the embedded software/services/startup
    /// snapshot (SnapshotService.Capture, reused verbatim), a hardware fingerprint (#953, from
    /// already-queried SystemSpecsViewModel/live PerformanceViewModel state - no new WMI reads),
    /// WinSAT scores (cache-first, only actually running `winsat formal` when `allowWinSatRun` is
    /// true and no cache exists - see WinSatService's remarks on why the automatic weekly capture
    /// must never pass true here), the last recorded boot duration (BootPerformanceService, already
    /// reused by the Startup tab), and a freshly-sampled disk latency reading (~1s,
    /// LogicalDisk\Avg. Disk sec/Transfer, the same counter/pattern TroubleshootService's disk
    /// checks already use).
    ///
    /// #957: `liveCpuPercent`/`liveCommittedGb` are recorded as given regardless of `isIdle` - a
    /// user-triggered capture is never blocked, only flagged (via the returned baseline's
    /// WasIdleAtCapture) - the caller (BaselineViewModel) decides whether to even attempt an
    /// automatic capture unless conditions are already idle.
    /// </summary>
    public static async Task<PerformanceBaseline> CaptureAsync(
        SystemSpecsViewModel systemSpecs,
        double ramTotalGb,
        double liveCpuPercent,
        double liveCommittedGb,
        bool isIdle,
        bool allowWinSatRun,
        string? label,
        bool wasAutomatic,
        CancellationToken ct)
    {
        var fingerprint = new HardwareFingerprint
        {
            CpuName = systemSpecs.CpuName,
            RamTotalGb = ramTotalGb,
            DiskModels = systemSpecs.Disks.Select(d => d.Primary).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            GpuNames = systemSpecs.Gpus.Select(g => g.Primary).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };

        // #950: SnapshotService.Capture (a registry Uninstall-key sweep + ServiceController +
        // startup-item sample) and the two other synchronous reads below are exactly the same
        // "genuinely a bit of I/O, not instant" work SummaryViewModel.SaveSnapshot already accepts
        // running inline for its own quick snapshot - but since this capture is already async
        // (the disk-latency sample and an optional WinSAT run both need to be), running them via
        // Task.Run costs nothing extra and keeps the UI thread free while they complete, matching
        // SystemSpecsViewModel.RefreshAsync's own "heavier WMI/registry query -> Task.Run" convention.
        SystemSnapshot snapshot = new();
        WinSatService.Scores? winsat = null;
        double? bootMs = null;
        await Task.Run(() =>
        {
            snapshot = SnapshotService.Capture();
            winsat = WinSatService.ReadCachedScoresOrNull();
            bootMs = BootPerformanceService.ReadLatest()?.TotalMs;
        }, ct);

        if (winsat is null && allowWinSatRun)
        {
            try { winsat = await WinSatService.RunFormalAsync(ct); }
            catch (OperationCanceledException) { /* caller's timeout - baseline still saves without WinSAT scores */ }
        }

        double? diskLatencyMs = await SampleDiskLatencyMsAsync(ct);

        return new PerformanceBaseline
        {
            CapturedAt = DateTime.Now,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            WasAutomatic = wasAutomatic,
            Snapshot = snapshot,
            Fingerprint = fingerprint,
            WinSatCpuScore = winsat?.Cpu,
            WinSatMemoryScore = winsat?.Memory,
            WinSatDiskScore = winsat?.Disk,
            WinSatOverallScore = winsat?.Overall,
            LastBootDurationMs = bootMs,
            IdleCpuPercent = liveCpuPercent,
            IdleRamCommittedGb = liveCommittedGb,
            IdleDiskLatencyMs = diskLatencyMs,
            WasIdleAtCapture = isIdle,
        };
    }

    /// <summary>LogicalDisk\Avg. Disk sec/Transfer for the system drive, sampled fresh over ~1
    /// second - the same counter/pattern TroubleshootService.CheckDiskLatencyVsThroughputAsync
    /// already uses, kept as its own small helper here rather than reused directly since that one
    /// also samples throughput and returns a DiagnosticStepResult, not a bare number.</summary>
    private static async Task<double?> SampleDiskLatencyMsAsync(CancellationToken ct)
    {
        string driveInstance = Environment.SystemDirectory[..1] + ":";
        PerformanceCounter? counter = null;
        try
        {
            try { counter = new PerformanceCounter("LogicalDisk", "Avg. Disk sec/Transfer", driveInstance, readOnly: true); }
            catch
            {
                try { counter = new PerformanceCounter("LogicalDisk", "Avg. Disk sec/Transfer", "_Total", readOnly: true); }
                catch { return null; }
            }

            counter.NextValue(); // first call on a rate counter always returns 0 - needs a second sample
            await Task.Delay(1000, ct);
            float latencySec = counter.NextValue();
            return latencySec * 1000.0;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
        finally
        {
            counter?.Dispose();
        }
    }

    public static void Save(PerformanceBaseline baseline)
    {
        Directory.CreateDirectory(BaselinesDirectory);
        string fileName = $"Baseline-{baseline.CapturedAt:yyyyMMdd_HHmmss}-{Guid.NewGuid():N}"[..40] + ".json";
        string path = Path.Combine(BaselinesDirectory, fileName);
        var json = JsonSerializer.Serialize(baseline, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    /// <summary>Every baseline currently on disk, oldest first (chronological - what #951's
    /// regression comparison and #954's trend chart both want directly).</summary>
    public static List<PerformanceBaseline> LoadAll()
    {
        var results = new List<PerformanceBaseline>();
        try
        {
            Directory.CreateDirectory(BaselinesDirectory);
            foreach (var file in Directory.GetFiles(BaselinesDirectory, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var baseline = JsonSerializer.Deserialize<PerformanceBaseline>(json);
                    if (baseline is not null) results.Add(baseline);
                }
                catch
                {
                    // One malformed baseline file shouldn't hide the rest.
                }
            }
        }
        catch
        {
            // Best-effort - a failed directory read just means "no baselines yet".
        }
        return results.OrderBy(b => b.CapturedAt).ToList();
    }

    /// <summary>#952: keeps only the most recent `max` baseline files on disk, deleting the oldest
    /// ones beyond that count. Called after every save (manual or automatic) so the cap applies
    /// uniformly regardless of which path added the latest baseline.</summary>
    public static void PruneToMax(int max)
    {
        if (max <= 0) return;
        try
        {
            var files = Directory.GetFiles(BaselinesDirectory, "*.json")
                .Select(f => (Path: f, Time: SafeCapturedAt(f)))
                .OrderBy(f => f.Time)
                .ToList();

            int excess = files.Count - max;
            for (int i = 0; i < excess; i++)
            {
                try { File.Delete(files[i].Path); } catch { /* best-effort - a locked/missing file just stays */ }
            }
        }
        catch
        {
            // Best-effort - pruning is a housekeeping nicety, not load-bearing.
        }
    }

    private static DateTime SafeCapturedAt(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var baseline = JsonSerializer.Deserialize<PerformanceBaseline>(json);
            if (baseline is not null) return baseline.CapturedAt;
        }
        catch { /* fall through to file time below */ }
        try { return File.GetCreationTime(path); } catch { return DateTime.MinValue; }
    }

    /// <summary>#951/#955/#956: the shared set of performance-metric comparison rows between two
    /// baselines - shown as-is (rows with no data on either side are simply skipped by the caller,
    /// via BaselineMetricComparison.SummaryText being empty).</summary>
    public static List<BaselineMetricComparison> CompareMetrics(PerformanceBaseline before, PerformanceBaseline after) => new()
    {
        new BaselineMetricComparison
        {
            MetricName = "Boot time", Unit = " s", LowerIsBetter = true,
            BeforeValue = before.LastBootDurationMs / 1000.0, AfterValue = after.LastBootDurationMs / 1000.0,
        },
        new BaselineMetricComparison
        {
            MetricName = "Idle CPU usage", Unit = "%", LowerIsBetter = true,
            BeforeValue = before.WasIdleAtCapture ? before.IdleCpuPercent : null,
            AfterValue = after.WasIdleAtCapture ? after.IdleCpuPercent : null,
        },
        new BaselineMetricComparison
        {
            MetricName = "Idle RAM committed", Unit = " GB", LowerIsBetter = true,
            BeforeValue = before.WasIdleAtCapture ? before.IdleRamCommittedGb : null,
            AfterValue = after.WasIdleAtCapture ? after.IdleRamCommittedGb : null,
        },
        new BaselineMetricComparison
        {
            MetricName = "Idle disk latency", Unit = " ms", LowerIsBetter = true,
            BeforeValue = before.WasIdleAtCapture ? before.IdleDiskLatencyMs : null,
            AfterValue = after.WasIdleAtCapture ? after.IdleDiskLatencyMs : null,
        },
        new BaselineMetricComparison
        {
            MetricName = "WinSAT CPU score", Unit = "", LowerIsBetter = false,
            BeforeValue = before.WinSatCpuScore, AfterValue = after.WinSatCpuScore,
        },
        new BaselineMetricComparison
        {
            MetricName = "WinSAT memory score", Unit = "", LowerIsBetter = false,
            BeforeValue = before.WinSatMemoryScore, AfterValue = after.WinSatMemoryScore,
        },
        new BaselineMetricComparison
        {
            MetricName = "WinSAT disk score", Unit = "", LowerIsBetter = false,
            BeforeValue = before.WinSatDiskScore, AfterValue = after.WinSatDiskScore,
        },
    };
}
