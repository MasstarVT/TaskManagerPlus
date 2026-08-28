using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Services;

/// <summary>
/// Backs the `--dump-json &lt;path&gt;` command-line flag (#77) - a one-shot snapshot of the
/// current metrics as JSON, written to disk and the process exits, no UI shown. Useful for
/// scripted/remote diagnostics (a scheduled task or remote-support script that wants a machine-
/// readable reading without driving the full GUI). Each service is constructed fresh and disposed
/// right after one sample - this is a one-shot CLI path, not something that needs the long-lived
/// sampler objects the running app keeps. The app's elevation requirement (app.manifest) still
/// applies here: launching with this flag still triggers the same UAC prompt as a normal launch.
///
/// suggestions.md #996: `--scan &lt;path&gt;` and `--collect &lt;path&gt;` extend this exact
/// one-shot construction style - see <see cref="ScanAsync"/>/<see cref="CollectAsync"/> below.
/// </summary>
public static class CliDumpService
{
    public static async Task DumpSnapshotAsync(string outputPath)
    {
        using var hardware = new HardwareMonitorService();
        // Rate-based counters (CPU%, disk/network throughput) read 0 on their very first sample
        // immediately after construction even though the constructor primes several of them - a
        // real reading needs one full tick's worth of elapsed time, which a one-shot CLI dump
        // can't wait around for without meaningfully slowing down a scripted caller. This is a
        // known, documented limitation of this snapshot mode, not a bug.
        var snapshot = hardware.Sample();

        var specsService = new SystemSpecsService();
        var specs = await specsService.QueryAsync();

        using var sensors = new SensorMonitorService();
        var readings = sensors.Sample();

        var result = new
        {
            timestamp = DateTime.Now.ToString("O"),
            cpu = new
            {
                name = specs.CpuName,
                percent = snapshot.CpuTotalPercent,
                clockGhz = snapshot.CpuCurrentClockGhz,
                baseClockGhz = snapshot.CpuBaseClockGhz,
                logicalProcessors = snapshot.LogicalProcessors,
                physicalCores = snapshot.PhysicalCores,
            },
            memory = new
            {
                usedBytes = snapshot.RamUsedBytes,
                totalBytes = snapshot.RamTotalBytes,
                percent = snapshot.RamPercent,
            },
            disk = new
            {
                activePercent = snapshot.DiskActivePercent,
                readBytesPerSec = snapshot.DiskReadBytesPerSec,
                writeBytesPerSec = snapshot.DiskWriteBytesPerSec,
            },
            network = new
            {
                receiveBytesPerSec = snapshot.NetworkReceiveBytesPerSec,
                sendBytesPerSec = snapshot.NetworkSendBytesPerSec,
            },
            sensors = readings
                .Where(r => r.Value.HasValue)
                .Select(r => new { hardware = r.HardwareName, sensor = r.SensorName, type = r.Type.ToString(), value = r.Value!.Value })
                .ToList(),
            system = new
            {
                os = specs.OsName,
                model = $"{specs.Manufacturer} {specs.Model}".Trim(),
                ramTotalBytes = specs.RamTotalBytes,
            },
        };

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>#996: `--scan &lt;path&gt;` - constructs the minimum ViewModels
    /// RulesEngineService.BuildMetricBag/Evaluate needs (mirroring MainViewModel's own composition
    /// order, minus everything the rules engine doesn't read), waits briefly for each poller's
    /// first tick (the same "a rate-based counter reads 0 on its very first sample" limitation
    /// DumpSnapshotAsync above already documents and accepts for this one-shot construction style),
    /// evaluates, and writes the findings as JSON.
    ///
    /// `outputPath` is dual-purpose by design: a path ending in ".json" is written to directly (the
    /// #996 ad-hoc/scripted case, matching --dump-json's own contract exactly); anything else is
    /// treated as a base folder and a fresh "&lt;path&gt;\yyyy-MM-dd_HHmmss\findings.json" is
    /// created under it - what #997's nightly scheduled task actually passes (a stable
    /// AppPaths.SettingsDirectory\UnattendedScans argument that must produce a NEW dated folder on
    /// every run, which a static schtasks command line can't do by injecting today's date itself).
    /// </summary>
    public static async Task ScanAsync(string outputPath, bool scrub, bool quiet)
    {
        _ = quiet; // #996: this path already shows no UI/dialogs regardless (same as --dump-json) - kept for CLI-contract symmetry with --collect.

        using var performance = new PerformanceViewModel();
        using var energyThermals = new EnergyThermalsViewModel(performance);
        var systemSpecs = new SystemSpecsViewModel();
        using var services = new ServicesViewModel();
        var processHistory = new ProcessHistoryService();
        using var leakWatch = new LeakWatchViewModel();
        using var processes = new ProcessesViewModel(processHistory, leakWatch);
        using var rulesEngine = new RulesEngineService(performance);

        await Task.Delay(2500);

        var bag = RulesEngineService.BuildMetricBag(performance, energyThermals, systemSpecs, services, processes, out var unavailable);
        var result = rulesEngine.Evaluate(bag, unavailable);
        var findings = SummaryViewModel.SortIssues(new List<HealthIssue>(result.Findings), HealthFindingSortMode.Impact);

        string json = JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true });
        if (scrub) json = await ScrubTextAsync(json);

        string resolvedPath = ResolveScanOutputPath(outputPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(resolvedPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(resolvedPath, json);
    }

    private static string ResolveScanOutputPath(string outputPath) =>
        outputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? outputPath
            : Path.Combine(outputPath, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"), "findings.json");

    /// <summary>#996: `--collect &lt;path&gt;` - runs EvidenceBundleService.CollectAsync with a
    /// default full item selection (BuildCatalog(), the same "collect everything" default the
    /// Evidence Bundle panel's own button uses) and writes the resulting zip to `path`. Same
    /// one-shot ViewModel construction as ScanAsync above (EvidenceBundleService.CollectContext
    /// needs the same handful of ViewModels the rules engine does).</summary>
    public static async Task CollectAsync(string outputZipPath, bool scrub, bool quiet)
    {
        _ = quiet; // #996: this path already shows no UI/dialogs (progress is only ever reported via IProgress<string>, which this CLI caller simply doesn't observe).

        using var performance = new PerformanceViewModel();
        using var energyThermals = new EnergyThermalsViewModel(performance);
        var systemSpecs = new SystemSpecsViewModel();
        using var services = new ServicesViewModel();
        var processHistory = new ProcessHistoryService();
        using var leakWatch = new LeakWatchViewModel();
        using var processes = new ProcessesViewModel(processHistory, leakWatch);
        using var rulesEngine = new RulesEngineService(performance);

        await Task.Delay(2500);

        var ctx = new EvidenceBundleService.CollectContext(performance, processes, energyThermals, systemSpecs, services, rulesEngine);
        string stagingDir = Path.Combine(Path.GetTempPath(), "TaskManagerPlus-CliCollect-" + Guid.NewGuid().ToString("N"));
        try
        {
            var items = EvidenceBundleService.BuildCatalog();
            var manifest = await EvidenceBundleService.CollectAsync(items, stagingDir, ctx, progress: null, CancellationToken.None);

            if (scrub)
            {
                var ruleSet = ScrubRulesService.Load();
                string? ssid = await ScrubRulesService.TryGetCurrentSsidAsync();
                var scrubber = PiiScrubber.Build(ruleSet, ssid);
                var scrubbableFiles = EvidenceBundleService.GetScrubbableFiles(items, manifest, stagingDir);
                var (scrubbedTextByPath, _) = EvidenceBundleService.PreviewScrub(scrubbableFiles, scrubber);
                EvidenceBundleService.ApplyScrubResults(scrubbedTextByPath);
                manifest.WasScrubbed = true;
                foreach (var entry in manifest.Entries.Where(e => e.Success))
                {
                    string fullPath = Path.Combine(stagingDir, entry.FileName.Replace('/', Path.DirectorySeparatorChar));
                    if (!scrubbedTextByPath.ContainsKey(fullPath) || !File.Exists(fullPath)) continue;
                    entry.SizeBytes = new FileInfo(fullPath).Length;
                    entry.Sha256 = EvidenceBundleService.ComputeSha256(fullPath);
                }
            }

            EvidenceBundleService.WriteManifest(manifest, stagingDir);

            var bag = RulesEngineService.BuildMetricBag(performance, energyThermals, systemSpecs, services, processes, out var unavailable);
            var result = rulesEngine.Evaluate(bag, unavailable);
            var findings = SummaryViewModel.SortIssues(new List<HealthIssue>(result.Findings), HealthFindingSortMode.Impact);
            var timeline = new List<TimelineEvent>();
            timeline.AddRange(TimelineService.GetReliabilityCrashEvents());
            timeline.AddRange(TimelineService.GetServiceFailureEvents());
            timeline.AddRange(TimelineService.GetWindowsUpdateEvents());
            timeline.AddRange(TimelineService.GetSoftwareInstallEvents());
            timeline.AddRange(ThermalEventLogService.ReadAll());
            timeline.AddRange(await TimelineService.GetDriverInstallEventsAsync());

            var html = EvidenceBundleService.BuildIndexHtml(manifest, findings, timeline.OrderByDescending(e => e.Timestamp).ToList(), systemSpecs, SummarySettingsService.Load().ReportTheme);
            File.WriteAllText(Path.Combine(stagingDir, "index.html"), html);

            var dir = Path.GetDirectoryName(Path.GetFullPath(outputZipPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            EvidenceBundleService.CreateZip(stagingDir, outputZipPath);
        }
        finally
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); }
            catch { /* best-effort - see EvidenceBundleViewModel.CleanupStaging's own remarks */ }
        }
    }

    private static async Task<string> ScrubTextAsync(string text)
    {
        var ruleSet = ScrubRulesService.Load();
        string? ssid = await ScrubRulesService.TryGetCurrentSsidAsync();
        var scrubber = PiiScrubber.Build(ruleSet, ssid);
        return scrubber.Scrub(text);
    }
}
