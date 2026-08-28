using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Text;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #200: "filtered evidence bundle export" - the integration point for most of this domain's other
/// services: EventTimelineService (the unified timeline), WerReportService (crash report copies),
/// EventLogService (the minidump list), EventKnowledgeBaseService (KB explanations for the top
/// signatures), and EventLogExplorerService (the actual .evtx exports). Produces a timestamped
/// folder under AppPaths.GetPath("EvidenceBundles") - a plain folder, not a .zip: item #200 allows
/// either, and a folder is the simpler deliverable with nothing extra to verify (System.IO.
/// Compression.ZipFile would be a one-line wrap around the finished folder if a caller ever wants
/// one, but nothing in this app currently needs it). Every sub-step is wrapped independently and
/// reported via EvidenceBundleStepResult - a slow/failing tool (msinfo32/dxdiag in particular can
/// hang or take minutes) degrades to "skipped, see Detail" rather than aborting the whole bundle,
/// per this app's "degrade, never fabricate" rule.
/// </summary>
public sealed class EvidenceBundleService
{
    private readonly EventLogExplorerService _explorer;
    private readonly EventTimelineService _timeline;
    private readonly WerReportService _wer;
    private readonly EventLogService _eventLog;
    private readonly EventKnowledgeBaseService _kb;
    private readonly EventAnomalyDetectionService _anomaly;

    public EvidenceBundleService()
    {
        _explorer = new EventLogExplorerService();
        _timeline = new EventTimelineService(_explorer);
        _wer = new WerReportService(_explorer);
        _eventLog = new EventLogService();
        _kb = new EventKnowledgeBaseService();
        _anomaly = new EventAnomalyDetectionService(_explorer);
    }

    public async Task<EvidenceBundleResult> ExportAsync(EvidenceBundleRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        string folder = CreateBundleFolder();
        var steps = new List<EvidenceBundleStepResult>();

        progress?.Report("Exporting filtered .evtx files...");
        string evtxDir = Path.Combine(folder, "EventLogs");
        Directory.CreateDirectory(evtxDir);
        foreach (var channel in request.Channels.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            steps.Add(await ExportChannelAsync(channel, request.StartUtc, request.EndUtc, evtxDir, ct));
        }

        progress?.Report("Copying WER crash reports...");
        steps.Add(await CopyWerReportsAsync(folder, request.StartUtc, request.EndUtc, ct));

        progress?.Report("Copying minidumps...");
        steps.Add(await CopyMinidumpsAsync(folder, request.StartUtc, request.EndUtc, ct));

        progress?.Report("Running systeminfo...");
        steps.Add(await CaptureSysteminfoAsync(folder, ct));

        progress?.Report("Running msinfo32 (this can take a minute)...");
        steps.Add(await CaptureMsinfo32Async(folder, ct));

        progress?.Report("Running dxdiag...");
        steps.Add(await CaptureDxdiagAsync(folder, ct));

        progress?.Report("Writing SUMMARY.md...");
        steps.Add(await GenerateSummaryAsync(folder, request, ct));

        return new EvidenceBundleResult { FolderPath = folder, Steps = steps };
    }

    private static string CreateBundleFolder()
    {
        string root = AppPaths.GetPath("EvidenceBundles");
        string folder = Path.Combine(root, $"Bundle-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    // ==================== filtered .evtx export ====================

    /// <summary>`wevtutil epl "&lt;channel&gt;" "&lt;out&gt;.evtx" "/q:&lt;time-bounded XPath&gt;"`
    /// followed by `wevtutil al "&lt;out&gt;.evtx" &lt;message-file paths&gt;` so provider messages
    /// still render on a machine that doesn't have those providers registered. The message-file
    /// paths are resolved via ProviderMetadata.MessageFilePath for every provider
    /// EventLogConfiguration reports as writing to this channel - a real, documented .NET API,
    /// rather than guessing a provider's message-DLL location from the registry directly. The `al`
    /// step is best-effort and never fails the export - an .evtx with unresolved message templates
    /// is still real, useful evidence.</summary>
    private static async Task<EvidenceBundleStepResult> ExportChannelAsync(string channel, DateTime startUtc, DateTime endUtc, string outDir, CancellationToken ct)
    {
        string stepName = $"Export channel: {channel}";
        string outPath = Path.Combine(outDir, SanitizeFileName(channel) + ".evtx");
        try
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* wevtutil epl refuses to overwrite - best-effort clear */ }

            string xpath = $"*[System[TimeCreated[@SystemTime>='{startUtc:o}'] and TimeCreated[@SystemTime<='{endUtc:o}']]]";
            var (output, exitCode) = await RunCapturedAsync("wevtutil.exe", $"epl \"{channel}\" \"{outPath}\" \"/q:{xpath}\"", 90000, ct);
            if (exitCode != 0 || !File.Exists(outPath))
            {
                return new EvidenceBundleStepResult { StepName = stepName, Succeeded = false, Detail = string.IsNullOrWhiteSpace(output) ? "wevtutil epl failed." : output.Trim() };
            }

            List<string> providers;
            try { using var cfg = new EventLogConfiguration(channel); providers = cfg.ProviderNames?.ToList() ?? new List<string>(); }
            catch { providers = new List<string>(); }

            var msgPaths = GetMessageFilePaths(providers);
            string alDetail = "no provider message files resolved";
            if (msgPaths.Count > 0)
            {
                string pathArgs = string.Join(' ', msgPaths.Select(p => $"\"{p}\""));
                var (alOutput, alExit) = await RunCapturedAsync("wevtutil.exe", $"al \"{outPath}\" {pathArgs}", 30000, ct);
                alDetail = alExit == 0 ? $"{msgPaths.Count} message file(s) associated" : $"message-file association failed ({alOutput.Trim()})";
            }

            return new EvidenceBundleStepResult { StepName = stepName, Succeeded = true, Detail = $"{Path.GetFileName(outPath)} - {alDetail}" };
        }
        catch (Exception ex)
        {
            return new EvidenceBundleStepResult { StepName = stepName, Succeeded = false, Detail = ex.Message };
        }
    }

    private static List<string> GetMessageFilePaths(IEnumerable<string> providerNames)
    {
        var paths = new List<string>();
        foreach (var name in providerNames)
        {
            try
            {
                using var meta = new ProviderMetadata(name);
                if (meta.MessageFilePath is { Length: > 0 } path && File.Exists(path)) paths.Add(path);
            }
            catch { /* provider not locally registered, or has no message file - skip it */ }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ==================== WER reports / minidumps ====================

    /// <summary>Reuses WerReportService.ReadReports (the same scan the Stability tab's crash-report
    /// cards already run) rather than re-deriving the ReportQueue/ReportArchive walk, and copies
    /// each in-window report's own Report.wer file.</summary>
    private async Task<EvidenceBundleStepResult> CopyWerReportsAsync(string folder, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        const string stepName = "Copy WER crash reports";
        try
        {
            var reports = await Task.Run(() => _wer.ReadReports(), ct);
            var inWindow = reports.Where(r => IsInWindow(r.Timestamp, startUtc, endUtc)).ToList();
            if (inWindow.Count == 0)
                return new EvidenceBundleStepResult { StepName = stepName, Succeeded = true, Detail = "No WER reports found in the selected window." };

            string werDir = Path.Combine(folder, "WerReports");
            Directory.CreateDirectory(werDir);
            int copied = 0;
            foreach (var r in inWindow)
            {
                try
                {
                    string src = Path.Combine(r.FolderPath, "Report.wer");
                    if (!File.Exists(src)) continue;
                    string destDir = Path.Combine(werDir, SanitizeFileName(new DirectoryInfo(r.FolderPath).Name));
                    Directory.CreateDirectory(destDir);
                    File.Copy(src, Path.Combine(destDir, "Report.wer"), overwrite: true);
                    copied++;
                }
                catch { /* one report locked/unreadable shouldn't block the rest */ }
            }
            return new EvidenceBundleStepResult { StepName = stepName, Succeeded = true, Detail = $"Copied {copied} of {inWindow.Count} Report.wer file(s)." };
        }
        catch (Exception ex)
        {
            return new EvidenceBundleStepResult { StepName = stepName, Succeeded = false, Detail = ex.Message };
        }
    }

    /// <summary>Reuses EventLogService's minidump correlation wholesale via its public Query() entry
    /// point (ReadMinidumps itself is private - Query() is the documented way to reach the same
    /// data this app's own Stability tab shows) rather than re-deriving the bugcheck-correlation
    /// logic here.</summary>
    private async Task<EvidenceBundleStepResult> CopyMinidumpsAsync(string folder, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        const string stepName = "Copy minidumps";
        try
        {
            var snapshot = await Task.Run(() => _eventLog.Query(), ct);
            var inWindow = snapshot.Minidumps.Where(d => IsInWindow(d.Timestamp, startUtc, endUtc)).ToList();
            if (inWindow.Count == 0)
                return new EvidenceBundleStepResult { StepName = stepName, Succeeded = true, Detail = "No minidumps found in the selected window." };

            string dumpsDir = Path.Combine(folder, "Minidumps");
            Directory.CreateDirectory(dumpsDir);
            string sourceDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
            int copied = 0;
            foreach (var d in inWindow)
            {
                try
                {
                    string src = Path.Combine(sourceDir, d.FileName);
                    if (!File.Exists(src)) continue;
                    File.Copy(src, Path.Combine(dumpsDir, d.FileName), overwrite: true);
                    copied++;
                }
                catch { /* one dump locked/unreadable shouldn't block the rest */ }
            }
            return new EvidenceBundleStepResult { StepName = stepName, Succeeded = true, Detail = $"Copied {copied} of {inWindow.Count} minidump(s)." };
        }
        catch (Exception ex)
        {
            return new EvidenceBundleStepResult { StepName = stepName, Succeeded = false, Detail = ex.Message };
        }
    }

    private static bool IsInWindow(DateTime localTimestamp, DateTime startUtc, DateTime endUtc)
        => localTimestamp >= startUtc.ToLocalTime() && localTimestamp <= endUtc.ToLocalTime();

    // ==================== systeminfo / msinfo32 / dxdiag ====================

    private static async Task<EvidenceBundleStepResult> CaptureSysteminfoAsync(string folder, CancellationToken ct)
    {
        const string stepName = "systeminfo";
        try
        {
            var (output, exitCode) = await RunCapturedAsync("systeminfo.exe", string.Empty, 60000, ct);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
                return new EvidenceBundleStepResult { StepName = stepName, Succeeded = false, Detail = "systeminfo.exe produced no output (or timed out)." };
            File.WriteAllText(Path.Combine(folder, "systeminfo.txt"), output);
            return new EvidenceBundleStepResult { StepName = stepName, Succeeded = true, Detail = "systeminfo.txt" };
        }
        catch (Exception ex)
        {
            return new EvidenceBundleStepResult { StepName = stepName, Succeeded = false, Detail = ex.Message };
        }
    }

    /// <summary>`msinfo32 /report &lt;path&gt;` - on some Windows versions the process itself exits
    /// well before the report file has finished being written, so this polls for the file's size to
    /// settle (unchanged across two consecutive 1-second checks) rather than trusting the process
    /// exit as "done", bounded by a generous overall timeout.</summary>
    private static async Task<EvidenceBundleStepResult> CaptureMsinfo32Async(string folder, CancellationToken ct)
    {
        const string stepName = "msinfo32 report";
        string outPath = Path.Combine(folder, "msinfo32.txt");
        try
        {
            var psi = new ProcessStartInfo("msinfo32.exe", $"/report \"{outPath}\"") { UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            if (proc is null) return new EvidenceBundleStepResult { StepName = stepName, Succeeded = false, Detail = "Couldn't start msinfo32.exe." };

            bool settled = await WaitForFileToSettleAsync(outPath, timeoutMs: 120000, ct);
            return settled
                ? new EvidenceBundleStepResult { StepName = stepName, Succeeded = true, Detail = "msinfo32.txt" }
                : new EvidenceBundleStepResult { StepName = stepName, Succeeded = false, Detail = "Timed out waiting for msinfo32's report to finish writing - skipped." };
        }
        catch (Exception ex)
        {
            return new EvidenceBundleStepResult { StepName = stepName, Succeeded = false, Detail = ex.Message };
        }
    }

    /// <summary>`dxdiag /t &lt;path&gt;` - same "process exit isn't the finish signal" caveat as
    /// msinfo32 above, same polling approach.</summary>
    private static async Task<EvidenceBundleStepResult> CaptureDxdiagAsync(string folder, CancellationToken ct)
    {
        const string stepName = "dxdiag report";
        string outPath = Path.Combine(folder, "dxdiag.txt");
        try
        {
            var psi = new ProcessStartInfo("dxdiag.exe", $"/t \"{outPath}\"") { UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            if (proc is null) return new EvidenceBundleStepResult { StepName = stepName, Succeeded = false, Detail = "Couldn't start dxdiag.exe." };

            bool settled = await WaitForFileToSettleAsync(outPath, timeoutMs: 90000, ct);
            return settled
                ? new EvidenceBundleStepResult { StepName = stepName, Succeeded = true, Detail = "dxdiag.txt" }
                : new EvidenceBundleStepResult { StepName = stepName, Succeeded = false, Detail = "Timed out waiting for dxdiag's report to finish writing - skipped." };
        }
        catch (Exception ex)
        {
            return new EvidenceBundleStepResult { StepName = stepName, Succeeded = false, Detail = ex.Message };
        }
    }

    private static async Task<bool> WaitForFileToSettleAsync(string path, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long lastSize = -1;
        int stableChecks = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1000, ct);
            if (!File.Exists(path)) continue;

            long size;
            try { size = new FileInfo(path).Length; } catch { continue; }

            if (size > 0 && size == lastSize)
            {
                stableChecks++;
                if (stableChecks >= 2) return true;
            }
            else
            {
                stableChecks = 0;
                lastSize = size;
            }
        }
        return File.Exists(path); // best-effort - a file that exists at all by the deadline is kept
    }

    // ==================== SUMMARY.md ====================

    /// <summary>Composes the timeline (EventTimelineService.BuildTimeline - the same call
    /// StabilityViewModel's own unified timeline card uses), the top event signatures in the window,
    /// and each signature's knowledge-base explanation (EventKnowledgeBaseService.Lookup) into one
    /// readable Markdown file - reusing every one of those services' existing logic rather than
    /// re-deriving any of it.</summary>
    private async Task<EvidenceBundleStepResult> GenerateSummaryAsync(string folder, EvidenceBundleRequest request, CancellationToken ct)
    {
        const string stepName = "SUMMARY.md";
        try
        {
            var snapshot = await Task.Run(() => _eventLog.Query(), ct);

            List<DateTime> bootMarkers;
            try { bootMarkers = await Task.Run(() => _anomaly.FindBootMarkers(30, ct), ct); }
            catch { bootMarkers = new List<DateTime>(); }

            List<RebootAttribution> shutdownMarkers;
            try { shutdownMarkers = await Task.Run(() => _timeline.ComputeRebootAttributions(), ct); }
            catch { shutdownMarkers = new List<RebootAttribution>(); }

            List<WerReportInfo> werReports;
            try { werReports = await Task.Run(() => _wer.ReadReports(), ct); }
            catch { werReports = new List<WerReportInfo>(); }

            var timeline = _timeline.BuildTimeline(snapshot.RecentEvents, snapshot.Minidumps, bootMarkers, shutdownMarkers, werReports: werReports)
                .Where(e => IsInWindow(e.Timestamp, request.StartUtc, request.EndUtc))
                .ToList();

            var topSignatures = snapshot.RecentEvents
                .Where(e => IsInWindow(e.TimeCreated, request.StartUtc, request.EndUtc))
                .GroupBy(e => (e.ProviderName, e.EventId))
                .Select(g => new { g.Key.ProviderName, g.Key.EventId, Count = g.Count(), Last = g.Max(e => e.TimeCreated) })
                .OrderByDescending(x => x.Count)
                .Take(15)
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("# Task Manager Plus - Evidence Bundle");
            sb.AppendLine();
            sb.AppendLine($"- Generated: {DateTime.Now:g}");
            sb.AppendLine($"- Window: {request.StartUtc.ToLocalTime():g} - {request.EndUtc.ToLocalTime():g}");
            sb.AppendLine($"- Channels exported: {string.Join(", ", request.Channels)}");
            sb.AppendLine();
            sb.AppendLine("Every heuristic/knowledge-base opinion below is informational only - a quick flag, not a diagnosis.");
            sb.AppendLine();
            sb.AppendLine("## Timeline");
            sb.AppendLine();
            if (timeline.Count == 0)
            {
                sb.AppendLine("_No timeline entries in this window._");
            }
            else
            {
                foreach (var e in timeline.Take(300))
                    sb.AppendLine($"- **{e.Timestamp:g}** [{e.SourceLabel}] {e.Title} - {e.Detail.Replace('\n', ' ').Replace('\r', ' ')}");
            }
            sb.AppendLine();
            sb.AppendLine("## Top event signatures in this window");
            sb.AppendLine();
            if (topSignatures.Count == 0)
            {
                sb.AppendLine("_No Critical/Error events in this window._");
            }
            else
            {
                sb.AppendLine("| Provider | Event ID | Count | Last seen | Knowledge base |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var s in topSignatures)
                {
                    var kb = _kb.Lookup(s.ProviderName, s.EventId);
                    string kbText = kb is null ? "(no entry)" : $"{kb.SeverityRank}: {kb.Meaning}".Replace('\n', ' ').Replace('|', '/');
                    sb.AppendLine($"| {s.ProviderName} | {s.EventId} | {s.Count} | {s.Last:g} | {kbText} |");
                }
            }

            File.WriteAllText(Path.Combine(folder, "SUMMARY.md"), sb.ToString());
            return new EvidenceBundleStepResult { StepName = stepName, Succeeded = true, Detail = "SUMMARY.md" };
        }
        catch (Exception ex)
        {
            return new EvidenceBundleStepResult { StepName = stepName, Succeeded = false, Detail = ex.Message };
        }
    }

    // ==================== shared helpers ====================

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Replace('/', '_');
    }

    private static async Task<(string Output, int ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

        var outputTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errorTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return ("(command timed out or was cancelled)", -1);
        }

        string output = (await outputTask) + (await errorTask);
        return (output, proc.ExitCode);
    }
}
