using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Services;

/// <summary>
/// suggestions.md #981-982/#986: builds a single "Collect everything" evidence-bundle ZIP -
/// msinfo32/dxdiag/systeminfo/driverquery/powercfg reports/pnputil/event-log exports/minidumps,
/// plus this app's own findings/timeline/baseline data, each collected by its own method under its
/// own timeout (<see cref="RunOneCollectorAsync"/>) so one hung/failed collector never takes the
/// whole run down - the same "a hung WMI query or a stuck call degrades one card, never the run"
/// house convention TroubleshootViewModel/SensorMonitorService already document, just applied here
/// to a batch of independent external-tool shell-outs instead of a single check. Every collector
/// records itself into an <see cref="EvidenceBundleManifest"/> entry, success or failure (#986),
/// and <see cref="BuildIndexHtml"/> renders that manifest into a human-readable index.html (#982)
/// using SummaryViewModel.BuildReportCss/BuildPrintPageBars verbatim - the same technique/CSS
/// #982's task notes explicitly ask to reuse, not a second copy.
///
/// Reuses TroubleshootService.RunCapturedAsync for every stdout-capturing shell-out (systeminfo,
/// driverquery, pnputil, powercfg, wevtutil) rather than duplicating that concurrent-read/bounded-
/// wait/kill-on-timeout logic again; msinfo32/dxdiag are the two exceptions - neither writes to
/// stdout (both are told to write straight to a file via their own /nfo or /t flag and just need a
/// "did the process exit before our timeout" wait, see RunProcessWithTimeoutAsync.
/// </summary>
public static class EvidenceBundleService
{
    /// <summary>Bundles the live-state references a collector might need - #916-927's
    /// RulesEngineService.BuildMetricBag/Evaluate call for the app's own findings collector, mirrors
    /// exactly what SummaryViewModel/BaselineService already take as constructor/method
    /// parameters (Services referencing a ViewModel directly is an established pattern in this
    /// codebase - see BaselineService.CaptureAsync's own SystemSpecsViewModel parameter).</summary>
    public sealed record CollectContext(
        PerformanceViewModel Performance,
        ProcessesViewModel Processes,
        EnergyThermalsViewModel EnergyThermals,
        SystemSpecsViewModel SystemSpecs,
        ServicesViewModel Services,
        RulesEngineService RulesEngine);

    /// <summary>#983: the checklist catalog - one row per artifact #981 lists, each with a
    /// one-line "what this contains and why it helps" description and a rough, labelled-as-a-guess
    /// size estimate. Sizes are deliberately round numbers (#983 is explicit this only needs to be
    /// directionally useful, not exact).</summary>
    public static List<EvidenceBundleItem> BuildCatalog() => new()
    {
        new EvidenceBundleItem { Kind = EvidenceBundleCollectorKind.MsInfo32, Title = "System information (msinfo32)",
            Description = "A full hardware/software/driver/BIOS inventory Windows itself collects for support - the single richest general-purpose diagnostic file. Can take a minute or two.",
            EstimatedSizeBytes = 3_000_000, IsTextScrubbable = true },
        new EvidenceBundleItem { Kind = EvidenceBundleCollectorKind.DxDiag, Title = "DirectX diagnostics (dxdiag)",
            Description = "Graphics/sound/input device and driver details - useful for display, GPU, and audio troubleshooting.",
            EstimatedSizeBytes = 150_000, IsTextScrubbable = true },
        new EvidenceBundleItem { Kind = EvidenceBundleCollectorKind.SystemInfo, Title = "System summary (systeminfo)",
            Description = "OS build, patch level, boot time, and memory summary in Windows' own plain-text format.",
            EstimatedSizeBytes = 6_000, IsTextScrubbable = true },
        new EvidenceBundleItem { Kind = EvidenceBundleCollectorKind.DriverQuery, Title = "Installed drivers (driverquery /v)",
            Description = "Every loaded driver, its version, and its start type - useful for spotting an outdated or third-party driver.",
            EstimatedSizeBytes = 200_000, IsTextScrubbable = true },
        new EvidenceBundleItem { Kind = EvidenceBundleCollectorKind.BatteryReport, Title = "Battery report (powercfg)",
            Description = "Design vs. full-charge capacity and recent usage history. Fails harmlessly on a desktop with no battery.",
            EstimatedSizeBytes = 80_000, IsTextScrubbable = true },
        new EvidenceBundleItem { Kind = EvidenceBundleCollectorKind.SleepStudy, Title = "Sleep study report (powercfg)",
            Description = "Modern Standby's own diagnostic report - what kept the system awake or drained battery during sleep.",
            EstimatedSizeBytes = 150_000, IsTextScrubbable = true },
        new EvidenceBundleItem { Kind = EvidenceBundleCollectorKind.EnergyReport, Title = "Energy efficiency report (powercfg)",
            Description = "A ~60-second trace of power-plan/USB/timer issues that hurt battery life - takes noticeably longer than the other reports.",
            EstimatedSizeBytes = 200_000, IsTextScrubbable = true },
        new EvidenceBundleItem { Kind = EvidenceBundleCollectorKind.PnpUtilDrivers, Title = "Driver store listing (pnputil)",
            Description = "Every driver package published into the driver store, with version and date - complements the loaded-driver list above.",
            EstimatedSizeBytes = 50_000, IsTextScrubbable = true },
        new EvidenceBundleItem { Kind = EvidenceBundleCollectorKind.EventLogSystem, Title = "System event log export (.evtx)",
            Description = "The full System event log, exported as a real .evtx a support engineer can open in Event Viewer - not just the recent-events summary this app already shows.",
            EstimatedSizeBytes = 20_000_000, IsTextScrubbable = false },
        new EvidenceBundleItem { Kind = EvidenceBundleCollectorKind.EventLogApplication, Title = "Application event log export (.evtx)",
            Description = "The full Application event log, same format as the System export above.",
            EstimatedSizeBytes = 15_000_000, IsTextScrubbable = false },
        new EvidenceBundleItem { Kind = EvidenceBundleCollectorKind.Minidumps, Title = "Recent crash minidumps",
            Description = "The newest few files from %SystemRoot%\\Minidump, if this system has ever blue-screened - the raw crash-dump files a support engineer would ask for first.",
            EstimatedSizeBytes = 2_000_000, IsTextScrubbable = false },
        new EvidenceBundleItem { Kind = EvidenceBundleCollectorKind.AppFindings, Title = "This app's current findings",
            Description = "Every Health Check finding this app currently has fired, sorted by impact - the same list the Summary tab shows.",
            EstimatedSizeBytes = 10_000, IsTextScrubbable = true },
        new EvidenceBundleItem { Kind = EvidenceBundleCollectorKind.AppTimeline, Title = "This app's Timeline events",
            Description = "Crashes, service failures, Windows Updates, driver/software installs, and thermal events this app's Timeline panel already tracks.",
            EstimatedSizeBytes = 30_000, IsTextScrubbable = true },
        new EvidenceBundleItem { Kind = EvidenceBundleCollectorKind.AppBaselines, Title = "Saved performance baselines",
            Description = "Any performance baselines you've saved from the Troubleshoot tab's Baselines panel - empty if you haven't saved one.",
            EstimatedSizeBytes = 20_000, IsTextScrubbable = true },
    };

    /// <summary>#981: runs every selected collector in turn (sequentially - a bulk "collect
    /// everything" operation, not something latency-sensitive enough to need parallelism, and
    /// sequential keeps progress reporting simple and avoids several shell-outs contending for the
    /// same event log/registry at once) into `stagingDir`, returning the completed manifest. Every
    /// collector's own failure/timeout is caught and recorded as a manifest entry rather than
    /// propagated - only genuine cancellation (the user cancelling the whole run) surfaces as an
    /// exception here.</summary>
    public static async Task<EvidenceBundleManifest> CollectAsync(
        IReadOnlyList<EvidenceBundleItem> selectedItems,
        string stagingDir,
        CollectContext context,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(stagingDir);
        var entries = new List<EvidenceBundleManifestEntry>();

        foreach (var item in selectedItems)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Collecting: {item.Title}…");
            var produced = await RunOneCollectorAsync(item, stagingDir, context);
            entries.AddRange(produced);
            bool ok = produced.Count > 0 && produced.All(e => e.Success);
            progress?.Report(ok ? $"Done: {item.Title}" : $"Not collected: {item.Title} ({produced.FirstOrDefault(e => !e.Success)?.FailureReason})");
        }

        return new EvidenceBundleManifest
        {
            GeneratedAtUtc = DateTime.UtcNow,
            MachineName = Environment.MachineName,
            AppVersion = typeof(EvidenceBundleService).Assembly.GetName().Version?.ToString() ?? "unknown",
            Entries = entries,
        };
    }

    /// <summary>Every text file (per EvidenceBundleItem.IsTextScrubbable) a just-completed
    /// CollectAsync pass wrote, as absolute paths - what #984's scrubbing pass runs over. Minidumps
    /// and .evtx exports are never included, even if somehow selected - see #984's task notes on
    /// why evtx specifically is excluded from text-scrubbing.</summary>
    public static List<string> GetScrubbableFiles(IReadOnlyList<EvidenceBundleItem> selectedItems, EvidenceBundleManifest manifest, string stagingDir)
    {
        var scrubbableKinds = selectedItems.Where(i => i.IsTextScrubbable).Select(i => i.Kind).ToHashSet();
        var files = new List<string>();
        foreach (var entry in manifest.Entries.Where(e => e.Success && e.FileName.Length > 0))
        {
            // Match a manifest entry back to its item by id prefix (minidump.<name>/app-baseline.<name>
            // entries never carry IsTextScrubbable=true to begin with, so they're excluded by the
            // HashSet check below regardless).
            var kind = IdToKind(entry.Id);
            if (kind is { } k && scrubbableKinds.Contains(k))
                files.Add(Path.Combine(stagingDir, entry.FileName));
        }
        return files;
    }

    private static EvidenceBundleCollectorKind? IdToKind(string id)
    {
        // Minidumps/baselines each produce one manifest entry per file (Id = "minidump.<name>" /
        // "app-baseline.<name>") rather than one entry for the whole collector - matched by prefix
        // so every individual baseline file still counts as scrubbable (IsTextScrubbable=true on
        // the AppBaselines catalog item) while every individual minidump stays excluded (it has no
        // matching case below, same as the .evtx exports).
        if (id.StartsWith("app-baseline.", StringComparison.OrdinalIgnoreCase)) return EvidenceBundleCollectorKind.AppBaselines;

        return id switch
        {
            "msinfo32" => EvidenceBundleCollectorKind.MsInfo32,
            "dxdiag" => EvidenceBundleCollectorKind.DxDiag,
            "systeminfo" => EvidenceBundleCollectorKind.SystemInfo,
            "driverquery" => EvidenceBundleCollectorKind.DriverQuery,
            "battery-report" => EvidenceBundleCollectorKind.BatteryReport,
            "sleep-study" => EvidenceBundleCollectorKind.SleepStudy,
            "energy-report" => EvidenceBundleCollectorKind.EnergyReport,
            "pnputil" => EvidenceBundleCollectorKind.PnpUtilDrivers,
            "app-findings" => EvidenceBundleCollectorKind.AppFindings,
            "app-timeline" => EvidenceBundleCollectorKind.AppTimeline,
            "app-baselines" => EvidenceBundleCollectorKind.AppBaselines,
            _ => null,
        };
    }

    /// <summary>#984: runs `scrubber` over every file in `filePaths` *in memory only* - nothing on
    /// disk changes yet. Returns the would-be scrubbed text per file (for ApplyScrubResults to
    /// write once the user confirms) plus the replacement summary the review screen renders -
    /// #984 is explicit this is a REVIEW step, so nothing is finalized until the caller has shown
    /// this list and the user has confirmed it.</summary>
    public static (Dictionary<string, string> ScrubbedTextByPath, List<ScrubReplacementSummary> Summaries) PreviewScrub(
        IEnumerable<string> filePaths, PiiScrubber scrubber)
    {
        var byPath = new Dictionary<string, string>();
        foreach (var path in filePaths)
        {
            try { byPath[path] = scrubber.Scrub(File.ReadAllText(path)); }
            catch { /* best-effort - an unreadable/locked file just isn't previewed/scrubbed */ }
        }
        return (byPath, scrubber.Summaries.ToList());
    }

    /// <summary>#984: writes the already-previewed scrub results to disk - only ever called after
    /// the user has confirmed the review screen built from PreviewScrub above.</summary>
    public static void ApplyScrubResults(Dictionary<string, string> scrubbedTextByPath)
    {
        foreach (var (path, text) in scrubbedTextByPath)
        {
            try { File.WriteAllText(path, text); }
            catch { /* best-effort - see PreviewScrub's remarks */ }
        }
    }

    public static void WriteManifest(EvidenceBundleManifest manifest, string stagingDir)
    {
        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(stagingDir, "manifest.json"), json);
    }

    public static void CreateZip(string stagingDir, string destinationZipPath)
    {
        if (File.Exists(destinationZipPath)) File.Delete(destinationZipPath);
        ZipFile.CreateFromDirectory(stagingDir, destinationZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }

    // ============================== per-collector dispatch ==============================

    private static async Task<List<EvidenceBundleManifestEntry>> RunOneCollectorAsync(EvidenceBundleItem item, string stagingDir, CollectContext ctx)
    {
        // #981: an outer cap on top of every specific collector's own internal timeout (belt and
        // suspenders, the same double-layered protection TroubleshootViewModel.RunOneStepAsync
        // applies via Task.WhenAny around each DiagnosticStep.Check) - guarantees a collector whose
        // internal timeout handling has a bug still can't hang the whole bundle.
        var overallTimeout = TimeSpan.FromSeconds(item.Kind switch
        {
            EvidenceBundleCollectorKind.MsInfo32 => 180,
            EvidenceBundleCollectorKind.EnergyReport => 110,
            EvidenceBundleCollectorKind.SleepStudy => 55,
            EvidenceBundleCollectorKind.DxDiag => 60,
            EvidenceBundleCollectorKind.EventLogSystem or EvidenceBundleCollectorKind.EventLogApplication => 60,
            _ => 40,
        });

        try
        {
            Task<List<EvidenceBundleManifestEntry>> task = item.Kind switch
            {
                EvidenceBundleCollectorKind.MsInfo32 => CollectMsInfo32Async(stagingDir),
                EvidenceBundleCollectorKind.DxDiag => CollectDxDiagAsync(stagingDir),
                EvidenceBundleCollectorKind.SystemInfo => CollectCapturedTextAsync("systeminfo", "System summary (systeminfo)",
                    "systeminfo.exe", string.Empty, "systeminfo.txt", stagingDir, 30_000),
                EvidenceBundleCollectorKind.DriverQuery => CollectCapturedTextAsync("driverquery", "Installed drivers (driverquery /v)",
                    "driverquery.exe", "/v /fo csv", "driverquery.csv", stagingDir, 30_000),
                EvidenceBundleCollectorKind.PnpUtilDrivers => CollectCapturedTextAsync("pnputil", "Driver store listing (pnputil)",
                    "pnputil.exe", "/enum-drivers", "pnputil-drivers.txt", stagingDir, 30_000),
                EvidenceBundleCollectorKind.BatteryReport => CollectPowercfgReportAsync("battery-report", "Battery report",
                    "/batteryreport", "battery-report.html", stagingDir, 25_000),
                EvidenceBundleCollectorKind.SleepStudy => CollectPowercfgReportAsync("sleep-study", "Sleep study report",
                    "/sleepstudy", "sleep-study.html", stagingDir, 40_000),
                EvidenceBundleCollectorKind.EnergyReport => CollectPowercfgReportAsync("energy-report", "Energy efficiency report",
                    "/energy", "energy-report.html", stagingDir, 100_000),
                EvidenceBundleCollectorKind.EventLogSystem => CollectWevtutilAsync("eventlog-system", "System event log export",
                    "System", "System.evtx", stagingDir),
                EvidenceBundleCollectorKind.EventLogApplication => CollectWevtutilAsync("eventlog-application", "Application event log export",
                    "Application", "Application.evtx", stagingDir),
                EvidenceBundleCollectorKind.Minidumps => CollectMinidumpsAsync(stagingDir),
                EvidenceBundleCollectorKind.AppFindings => CollectAppFindingsAsync(stagingDir, ctx),
                EvidenceBundleCollectorKind.AppTimeline => CollectAppTimelineAsync(stagingDir),
                EvidenceBundleCollectorKind.AppBaselines => CollectAppBaselinesAsync(stagingDir),
                _ => Task.FromResult(new List<EvidenceBundleManifestEntry> { FailureEntry(item.Kind.ToString(), item.Title, "-", "Unknown collector.") }),
            };

            return await task.WaitAsync(overallTimeout);
        }
        catch (Exception ex)
        {
            string reason = ex is TimeoutException or OperationCanceledException
                ? $"Timed out after {overallTimeout.TotalSeconds:0}s."
                : ex.Message;
            return new List<EvidenceBundleManifestEntry> { FailureEntry(item.Kind.ToString(), item.Title, item.Title, reason) };
        }
    }

    // ============================== individual collectors ==============================

    /// <summary>#981: msinfo32 has no timeout of its own and is known to occasionally sit for a
    /// long time collecting a slow WMI class - launched directly via Process.Start (not
    /// RunCapturedAsync, since /nfo writes straight to the given file and there's no stdout to
    /// capture) so "still running past our own timeout" can be treated as a failed/skipped
    /// collector, never a hang for the whole bundle.</summary>
    private static async Task<List<EvidenceBundleManifestEntry>> CollectMsInfo32Async(string stagingDir)
    {
        const string fileName = "msinfo32-report.nfo";
        string sourceCommand = $"msinfo32.exe /nfo \"{fileName}\"";
        var (completed, error) = await RunProcessWithTimeoutAsync("msinfo32.exe", $"/nfo \"{Path.Combine(stagingDir, fileName)}\"", timeoutMs: 170_000);
        return One(BuildEntry("msinfo32", "System information (msinfo32)", sourceCommand, stagingDir, fileName, completed, error));
    }

    private static async Task<List<EvidenceBundleManifestEntry>> CollectDxDiagAsync(string stagingDir)
    {
        const string fileName = "dxdiag-report.txt";
        string sourceCommand = $"dxdiag.exe /t \"{fileName}\"";
        var (completed, error) = await RunProcessWithTimeoutAsync("dxdiag.exe", $"/t \"{Path.Combine(stagingDir, fileName)}\"", timeoutMs: 50_000);
        return One(BuildEntry("dxdiag", "DirectX diagnostics (dxdiag)", sourceCommand, stagingDir, fileName, completed, error));
    }

    /// <summary>Runs a tool that writes to stdout (systeminfo, driverquery, pnputil) via
    /// TroubleshootService.RunCapturedAsync and redirects that captured output into a file.</summary>
    private static async Task<List<EvidenceBundleManifestEntry>> CollectCapturedTextAsync(
        string id, string title, string exe, string args, string fileName, string stagingDir, int timeoutMs)
    {
        string sourceCommand = string.IsNullOrEmpty(args) ? exe : $"{exe} {args}";
        try
        {
            var (output, exitCode) = await TroubleshootService.RunCapturedAsync(exe, args, timeoutMs);
            if (exitCode is null)
                return One(FailureEntry(id, title, sourceCommand, $"Timed out after {timeoutMs / 1000}s."));

            await File.WriteAllTextAsync(Path.Combine(stagingDir, fileName), output);
            return One(BuildEntry(id, title, sourceCommand, stagingDir, fileName, true, null));
        }
        catch (Exception ex)
        {
            return One(FailureEntry(id, title, sourceCommand, ex.Message));
        }
    }

    /// <summary>powercfg's report generators (/batteryreport, /sleepstudy, /energy) write directly
    /// to the path given via /output - a nonzero exit or a missing output file most commonly means
    /// "no battery on this machine" (battery/sleep study) rather than a real failure, so that's
    /// recorded as a plain, non-alarming FailureReason rather than surfaced as an error (#981:
    /// "a desktop with no battery just means the batteryreport call fails, recorded as such, not
    /// fatal to the rest").</summary>
    private static async Task<List<EvidenceBundleManifestEntry>> CollectPowercfgReportAsync(
        string id, string title, string flag, string fileName, string stagingDir, int timeoutMs)
    {
        string fullPath = Path.Combine(stagingDir, fileName);
        string sourceCommand = $"powercfg.exe {flag} /output \"{fileName}\"";
        try
        {
            var (output, exitCode) = await TroubleshootService.RunCapturedAsync("powercfg.exe", $"{flag} /output \"{fullPath}\"", timeoutMs);
            if (exitCode is null)
                return One(FailureEntry(id, title, sourceCommand, $"Timed out after {timeoutMs / 1000}s."));

            if (exitCode != 0 || !File.Exists(fullPath))
            {
                string reason = TroubleshootService.Truncate(output.Trim(), 200);
                return One(FailureEntry(id, title, sourceCommand, reason.Length > 0 ? reason : "No report was produced (e.g. no battery present on this system)."));
            }
            return One(BuildEntry(id, title, sourceCommand, stagingDir, fileName, true, null));
        }
        catch (Exception ex)
        {
            return One(FailureEntry(id, title, sourceCommand, ex.Message));
        }
    }

    private static async Task<List<EvidenceBundleManifestEntry>> CollectWevtutilAsync(string id, string title, string logName, string fileName, string stagingDir)
    {
        string fullPath = Path.Combine(stagingDir, fileName);
        string sourceCommand = $"wevtutil.exe epl {logName} \"{fileName}\"";
        try
        {
            if (File.Exists(fullPath)) File.Delete(fullPath); // wevtutil refuses to overwrite an existing file
            var (output, exitCode) = await TroubleshootService.RunCapturedAsync("wevtutil.exe", $"epl {logName} \"{fullPath}\"", timeoutMs: 45_000);
            if (exitCode is null)
                return One(FailureEntry(id, title, sourceCommand, "Timed out."));
            if (exitCode != 0 || !File.Exists(fullPath))
                return One(FailureEntry(id, title, sourceCommand, TroubleshootService.Truncate(output.Trim(), 200)));
            return One(BuildEntry(id, title, sourceCommand, stagingDir, fileName, true, null));
        }
        catch (Exception ex)
        {
            return One(FailureEntry(id, title, sourceCommand, ex.Message));
        }
    }

    /// <summary>#981: copies the newest few (5) *.dmp files from %SystemRoot%\Minidump verbatim -
    /// each dump gets its own manifest entry (a real per-file hash, per #986) rather than one
    /// combined entry for the whole folder.</summary>
    private static Task<List<EvidenceBundleManifestEntry>> CollectMinidumpsAsync(string stagingDir)
    {
        const int maxDumps = 5;
        const string sourceCommand = "Copy of %SystemRoot%\\Minidump\\*.dmp (newest files)";
        var entries = new List<EvidenceBundleManifestEntry>();
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
            if (!Directory.Exists(dir))
                return Task.FromResult(One(FailureEntry("minidumps", "Recent crash minidumps", sourceCommand, "No minidumps found (no Minidump folder on this system).")));

            var files = Directory.GetFiles(dir, "*.dmp").Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Take(maxDumps).ToList();
            if (files.Count == 0)
                return Task.FromResult(One(FailureEntry("minidumps", "Recent crash minidumps", sourceCommand, "No minidumps found.")));

            Directory.CreateDirectory(Path.Combine(stagingDir, "Minidumps"));
            foreach (var f in files)
            {
                string relative = Path.Combine("Minidumps", f.Name);
                try
                {
                    File.Copy(f.FullName, Path.Combine(stagingDir, relative), overwrite: true);
                    entries.Add(BuildEntry($"minidump.{f.Name}", $"Minidump: {f.Name}", $"Copy of %SystemRoot%\\Minidump\\{f.Name}", stagingDir, relative, true, null));
                }
                catch (Exception ex)
                {
                    entries.Add(FailureEntry($"minidump.{f.Name}", $"Minidump: {f.Name}", sourceCommand, ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            entries.Add(FailureEntry("minidumps", "Recent crash minidumps", sourceCommand, ex.Message));
        }
        return Task.FromResult(entries);
    }

    /// <summary>#981: reuses RulesEngineService.BuildMetricBag/Evaluate exactly as SummaryViewModel
    /// does - a fresh evaluation, not a stale cached list - then SummaryViewModel.SortIssues (#982:
    /// "reuse the sort logic ... in SummaryViewModel") orders it the same way the Health Check card
    /// does before serializing to AppData\findings.json.</summary>
    private static Task<List<EvidenceBundleManifestEntry>> CollectAppFindingsAsync(string stagingDir, CollectContext ctx)
    {
        const string sourceCommand = "This app's rules engine (RulesEngineService.Evaluate)";
        string relative = Path.Combine("AppData", "findings.json");
        try
        {
            var bag = RulesEngineService.BuildMetricBag(ctx.Performance, ctx.EnergyThermals, ctx.SystemSpecs, ctx.Services, ctx.Processes, out var unavailable);
            var result = ctx.RulesEngine.Evaluate(bag, unavailable);
            var sorted = SummaryViewModel.SortIssues(new List<HealthIssue>(result.Findings), HealthFindingSortMode.Impact);

            WriteJson(stagingDir, relative, sorted);
            return Task.FromResult(One(BuildEntry("app-findings", "This app's current findings", sourceCommand, stagingDir, relative, true, null)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(One(FailureEntry("app-findings", "This app's current findings", sourceCommand, ex.Message)));
        }
    }

    /// <summary>#981: the same lanes TimelineViewModel.LoadAsync aggregates (minus perf spikes,
    /// which needs a CSV log already replayed this session - not something a bundle collection
    /// should trigger on its own) - every TimelineService method already degrades to an empty list
    /// on its own failure (see TimelineService's remarks), so this only needs to catch a genuinely
    /// unexpected exception.</summary>
    private static async Task<List<EvidenceBundleManifestEntry>> CollectAppTimelineAsync(string stagingDir)
    {
        const string sourceCommand = "This app's Timeline aggregator (TimelineService)";
        string relative = Path.Combine("AppData", "timeline.json");
        try
        {
            var events = new List<TimelineEvent>();
            events.AddRange(TimelineService.GetReliabilityCrashEvents());
            events.AddRange(TimelineService.GetServiceFailureEvents());
            events.AddRange(TimelineService.GetWindowsUpdateEvents());
            events.AddRange(TimelineService.GetSoftwareInstallEvents());
            events.AddRange(ThermalEventLogService.ReadAll());
            events.AddRange(await TimelineService.GetDriverInstallEventsAsync());
            events = events.OrderByDescending(e => e.Timestamp).ToList();

            WriteJson(stagingDir, relative, events);
            return One(BuildEntry("app-timeline", "This app's Timeline events", sourceCommand, stagingDir, relative, true, null));
        }
        catch (Exception ex)
        {
            return One(FailureEntry("app-timeline", "This app's Timeline events", sourceCommand, ex.Message));
        }
    }

    /// <summary>#981: copies whatever's already sitting in AppPaths.SettingsDirectory\Baselines\
    /// verbatim (already-serialized PerformanceBaseline JSON files - nothing to re-derive), one
    /// manifest entry per file.</summary>
    private static Task<List<EvidenceBundleManifestEntry>> CollectAppBaselinesAsync(string stagingDir)
    {
        const string sourceCommand = "Saved performance baselines (Baselines folder)";
        var entries = new List<EvidenceBundleManifestEntry>();
        try
        {
            var baselinesDir = AppPaths.GetPath("Baselines");
            var files = Directory.Exists(baselinesDir) ? Directory.GetFiles(baselinesDir, "*.json") : Array.Empty<string>();
            if (files.Length == 0)
            {
                entries.Add(FailureEntry("app-baselines", "Saved performance baselines", sourceCommand, "No baselines have been saved yet."));
                return Task.FromResult(entries);
            }

            Directory.CreateDirectory(Path.Combine(stagingDir, "AppData", "Baselines"));
            foreach (var f in files)
            {
                string name = Path.GetFileName(f);
                string relative = Path.Combine("AppData", "Baselines", name);
                try
                {
                    File.Copy(f, Path.Combine(stagingDir, relative), overwrite: true);
                    entries.Add(BuildEntry($"app-baseline.{name}", $"Baseline: {name}", sourceCommand, stagingDir, relative, true, null));
                }
                catch (Exception ex)
                {
                    entries.Add(FailureEntry($"app-baseline.{name}", $"Baseline: {name}", sourceCommand, ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            entries.Add(FailureEntry("app-baselines", "Saved performance baselines", sourceCommand, ex.Message));
        }
        return Task.FromResult(entries);
    }

    // ============================== small shared helpers ==============================

    private static List<EvidenceBundleManifestEntry> One(EvidenceBundleManifestEntry entry) => new() { entry };

    private static void WriteJson<T>(string stagingDir, string relativePath, T value)
    {
        string fullPath = Path.Combine(stagingDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static EvidenceBundleManifestEntry BuildEntry(string id, string title, string sourceCommand, string stagingDir, string relativeFileName, bool produced, string? failureReason)
    {
        string fullPath = Path.Combine(stagingDir, relativeFileName);
        if (produced && File.Exists(fullPath))
        {
            var info = new FileInfo(fullPath);
            return new EvidenceBundleManifestEntry
            {
                Id = id, Title = title, SourceCommand = sourceCommand, CollectedAtUtc = DateTime.UtcNow,
                Success = true, FileName = relativeFileName.Replace('\\', '/'), SizeBytes = info.Length, Sha256 = ComputeSha256(fullPath),
            };
        }
        return FailureEntry(id, title, sourceCommand, failureReason ?? "No output produced.");
    }

    private static EvidenceBundleManifestEntry FailureEntry(string id, string title, string sourceCommand, string reason) => new()
    {
        Id = id, Title = title, SourceCommand = sourceCommand, CollectedAtUtc = DateTime.UtcNow, Success = false, FailureReason = reason,
    };

    /// <summary>internal (not private) so EvidenceBundleViewModel can re-hash a file that #984's
    /// scrub pass rewrote after this entry was first built (see EvidenceBundleManifestEntry.Sha256's
    /// remarks).</summary>
    internal static string ComputeSha256(string filePath)
    {
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Waits for `exe` to exit under `timeoutMs`, killing it (and its child tree) on
    /// timeout - the same shape TroubleshootService.RunCapturedAsync uses, minus stdout/stderr
    /// redirection (msinfo32/dxdiag both write straight to the file we pass them, not stdout).</summary>
    private static async Task<(bool Completed, string? Error)> RunProcessWithTimeoutAsync(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
                return (true, null);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return (false, $"Timed out after {timeoutMs / 1000}s.");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ============================== #982: index.html ==============================

    /// <summary>#982: a self-contained index.html - inline CSS (SummaryViewModel.BuildReportCss,
    /// the exact technique #982's task notes ask to reuse), the machine summary, findings sorted by
    /// impact, a chronological timeline table, and a linked file listing with size/hash - so a
    /// bundle recipient with no access to this app can still make sense of what's inside without
    /// unzipping into a dozen separate tools.</summary>
    public static string BuildIndexHtml(EvidenceBundleManifest manifest, List<HealthIssue> findings, List<TimelineEvent> timelineEvents, SystemSpecsViewModel specs, ReportTheme theme)
    {
        static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        var generatedLocal = manifest.GeneratedAtUtc.ToLocalTime();

        Line("<!doctype html><html><head><meta charset=\"utf-8\">");
        Line($"<title>Evidence bundle - {Esc(manifest.MachineName)} - {Esc(generatedLocal.ToString("F"))}</title>");
        Line("<style>" + SummaryViewModel.BuildReportCss(theme) + "</style></head><body>");
        Line(SummaryViewModel.BuildPrintPageBars(manifest.MachineName, generatedLocal));

        Line($"<h1>Evidence bundle</h1><p class=\"muted\">Generated {Esc(generatedLocal.ToString("F"))} on {Esc(manifest.MachineName)}" +
             (manifest.WasScrubbed ? " — personal info was scrubbed before this bundle was built (see manifest.json)." : string.Empty) + "</p>");

        Line("<h2>Machine</h2><table>");
        Line($"<tr><td>OS</td><td>{Esc(specs.OsName)} ({Esc(specs.OsDetails)})</td></tr>");
        Line($"<tr><td>Model</td><td>{Esc(specs.SystemModel)}</td></tr>");
        Line($"<tr><td>CPU</td><td>{Esc(specs.CpuName)} — {Esc(specs.CpuDetails)}</td></tr>");
        Line($"<tr><td>RAM</td><td>{Esc(specs.RamTotal)} ({Esc(specs.RamDetails)})</td></tr>");
        Line($"<tr><td>Motherboard</td><td>{Esc(specs.Motherboard)}</td></tr>");
        Line($"<tr><td>BIOS</td><td>{Esc(specs.BiosVersion)}</td></tr>");
        Line("</table>");

        Line("<h2>Findings (sorted by impact)</h2>");
        if (findings.Count == 0)
        {
            Line("<p class=\"ok\">No issues detected at collection time.</p>");
        }
        else
        {
            Line("<table><tr><th>Severity</th><th>Finding</th><th>Confidence</th><th>Impact</th><th>Docs</th></tr>");
            foreach (var f in findings)
            {
                string cls = f.Severity == RuleSeverity.High ? "crit" : f.Severity == RuleSeverity.Info ? "muted" : "warn";
                // #992: DocsUrl carried through into the bundle's index.html too, same as the
                // Markdown/HTML diagnostic reports.
                string docsCell = string.IsNullOrEmpty(f.DocsUrl) ? string.Empty : $"<a href=\"{Esc(f.DocsUrl)}\">Learn more</a>";
                Line($"<tr><td class=\"{cls}\">{Esc(f.Severity.ToString())}</td>" +
                     $"<td>{Esc(f.Title ?? f.Message)}<br/><span class=\"muted\">{Esc(f.Message)}</span></td>" +
                     $"<td>{Esc(f.ConfidenceWord)}</td><td>{Esc(f.ImpactText ?? string.Empty)}</td><td>{docsCell}</td></tr>");
            }
            Line("</table>");
        }

        Line("<h2>Timeline</h2>");
        if (timelineEvents.Count == 0)
        {
            Line("<p class=\"muted\">No dated events collected.</p>");
        }
        else
        {
            Line("<table><thead><tr><th>When</th><th>Lane</th><th>Title</th><th>Detail</th></tr></thead><tbody>");
            foreach (var e in timelineEvents.OrderByDescending(e => e.Timestamp).Take(300))
            {
                string cls = e.IsFailure ? "crit" : string.Empty;
                Line($"<tr><td>{e.Timestamp:g}</td><td class=\"{cls}\">{Esc(e.LaneDisplayName)}</td><td>{Esc(e.Title)}</td><td>{Esc(e.Detail)}</td></tr>");
            }
            Line("</tbody></table>");
        }

        // #982: linked file listing, every other file in the archive with its size. #988: the
        // relative href is also shown as visible text (a "noprint" span, hidden on screen behind
        // the link but present so a printed page - which can't click a link - still shows the path).
        Line("<h2>Files in this bundle</h2>");
        var succeeded = manifest.Entries.Where(e => e.Success).ToList();
        if (succeeded.Count == 0)
        {
            Line("<p class=\"warn\">Nothing was collected.</p>");
        }
        else
        {
            Line("<table><thead><tr><th>File</th><th>Source</th><th>Size</th><th>SHA-256</th><th>Collected</th></tr></thead><tbody>");
            foreach (var e in succeeded)
            {
                Line($"<tr><td><a href=\"{Esc(e.FileName)}\">{Esc(e.FileName)}</a></td>" +
                     $"<td class=\"muted\">{Esc(e.SourceCommand)}</td><td>{Formatting.FormatBytes(e.SizeBytes)}</td>" +
                     $"<td class=\"muted\" style=\"font-family:Consolas,monospace;font-size:11px\">{Esc(e.Sha256)}</td>" +
                     $"<td>{e.CollectedAtUtc.ToLocalTime():g}</td></tr>");
            }
            Line("</tbody></table>");
        }

        // #986: what's missing and why - rendered right alongside the file listing so a recipient
        // sees the full picture in one place.
        var failed = manifest.Entries.Where(e => !e.Success).ToList();
        if (failed.Count > 0)
        {
            Line("<h2>Not collected</h2>");
            Line("<table><thead><tr><th>Item</th><th>Reason</th></tr></thead><tbody>");
            foreach (var e in failed)
                Line($"<tr><td>{Esc(e.Title)}</td><td class=\"warn\">{Esc(e.FailureReason)}</td></tr>");
            Line("</tbody></table>");
        }

        Line("</body></html>");
        return sb.ToString();
    }
}
