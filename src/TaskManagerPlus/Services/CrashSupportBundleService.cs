using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 21, item 100: "one-click crash support bundle" - the exact package a support forum or
/// technician asks for, assembled in one go: the selected minidumps and WER reports (item 49's
/// multi-select, finally consumed here), a full `msinfo32 /nfo` system-information export, System
/// and Application event-log exports (`wevtutil epl`), a verbose driver inventory (`driverquery
/// /v /fo csv`), the crash-dump-configuration read-out (CrashDumpConfigService, items 71-80) and a
/// generated Markdown crash summary pulled from CrashCorrelationService's own cluster/timeline data
/// (items 89-94) - zipped with the framework's own System.IO.Compression, no new dependency.
///
/// Every step below is wrapped independently (one failed export - msinfo32 missing, wevtutil
/// denied, a locked dump file - doesn't stop the rest of the bundle from being built), per
/// CLAUDE.md's "degrade gracefully" convention; BuildAsync's own returned message lists exactly
/// which pieces succeeded and which didn't; rather than an all-or-nothing outcome.
/// </summary>
public static class CrashSupportBundleService
{
    // Generous but bounded - msinfo32 in particular can take a couple of minutes on a machine
    // with a lot of installed software/drivers; nothing else here should ever come close to this.
    private const int ExportTimeoutMs = 5 * 60 * 1000;

    // The "what changed before this started" panel (items 92-94) is only ever computed for a
    // cluster the user actually expands in the UI - re-running it for every cluster here would be
    // needless cost for clusters nobody's investigating. Capped to the handful of most significant
    // clusters (BuildClusters already sorts by count then recency) instead.
    private const int MaxClustersWithWhatChanged = 5;

    public static async Task<(bool Ok, string Message, string? FilePath)> BuildAsync(
        List<string> selectedDumpFilePaths,
        List<string> selectedWerReportFolders,
        CrashDumpConfiguration? crashDumpConfig,
        List<CrashCluster> clusters,
        string outputZipPath)
    {
        var notes = new List<string>();
        string workDir = Path.Combine(Path.GetTempPath(), "TaskManagerPlus-SupportBundle-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(workDir);

            // ---- Selected minidumps -----------------------------------------------------------
            int dumpsCopied = CopySelectedDumps(selectedDumpFilePaths, workDir, notes);

            // ---- Selected WER report folders ----------------------------------------------------
            int werCopied = CopySelectedWerReports(selectedWerReportFolders, workDir, notes);

            if (dumpsCopied == 0 && werCopied == 0)
                notes.Add("No minidumps or WER reports were selected - the bundle only contains system information and logs. Select rows on the Dump analysis / Error reports cards first for a more complete bundle.");

            // ---- msinfo32 /nfo -------------------------------------------------------------------
            await RunExportStepAsync(notes, "System information (msinfo32)", async () =>
            {
                string nfoPath = Path.Combine(workDir, "SystemInfo.nfo");
                var (_, exitCode) = await RunCapturedAsync("msinfo32.exe", $"/nfo \"{nfoPath}\"", ExportTimeoutMs);
                if (exitCode is null) return "msinfo32 didn't finish in time and was stopped.";
                return File.Exists(nfoPath) ? null : "msinfo32 finished but didn't produce a report file.";
            });

            // ---- Event log exports -----------------------------------------------------------
            await RunExportStepAsync(notes, "System event log export", async () =>
            {
                string path = Path.Combine(workDir, "System.evtx");
                var (output, exitCode) = await RunCapturedAsync("wevtutil.exe", $"epl System \"{path}\"", 60000);
                return exitCode == 0 ? null : $"wevtutil couldn't export the System log: {Truncate(output)}";
            });

            await RunExportStepAsync(notes, "Application event log export", async () =>
            {
                string path = Path.Combine(workDir, "Application.evtx");
                var (output, exitCode) = await RunCapturedAsync("wevtutil.exe", $"epl Application \"{path}\"", 60000);
                return exitCode == 0 ? null : $"wevtutil couldn't export the Application log: {Truncate(output)}";
            });

            // ---- Driver inventory ------------------------------------------------------------
            await RunExportStepAsync(notes, "Driver inventory (driverquery)", async () =>
            {
                var (output, exitCode) = await RunCapturedAsync("driverquery.exe", "/v /fo csv", 60000);
                if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
                    return $"driverquery didn't return a driver list: {Truncate(output)}";
                await File.WriteAllTextAsync(Path.Combine(workDir, "Drivers.csv"), output, Encoding.UTF8);
                return null;
            });

            // ---- Crash-dump configuration read-out --------------------------------------------
            try
            {
                await File.WriteAllTextAsync(Path.Combine(workDir, "CrashDumpConfiguration.txt"),
                    BuildCrashDumpConfigText(crashDumpConfig), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                notes.Add($"Crash-dump configuration read-out: couldn't write it to the bundle ({ex.Message}).");
            }

            // ---- Generated Markdown crash summary ----------------------------------------------
            try
            {
                string summary = await BuildMarkdownSummaryAsync(clusters, crashDumpConfig);
                await File.WriteAllTextAsync(Path.Combine(workDir, "CrashSummary.md"), summary, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                notes.Add($"Crash summary: couldn't generate it ({ex.Message}).");
            }

            // ---- Zip it all up -----------------------------------------------------------------
            if (File.Exists(outputZipPath)) File.Delete(outputZipPath);
            ZipFile.CreateFromDirectory(workDir, outputZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

            string summaryLine = $"Support bundle saved to {outputZipPath} ({dumpsCopied} dump(s), {werCopied} WER report(s)).";
            return (true, notes.Count == 0 ? summaryLine : summaryLine + " " + string.Join(" ", notes), outputZipPath);
        }
        catch (Exception ex)
        {
            return (false, $"Couldn't build the support bundle: {ex.Message}", null);
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); }
            catch { /* best-effort cleanup - a leftover temp folder isn't worth failing the whole action over */ }
        }
    }

    /// <summary>Runs one export step, catching any exception it throws and folding either that or
    /// whatever non-null string the step itself returns (its own best-effort failure reason) into
    /// the shared notes list - keeps BuildAsync's own body from repeating the same try/catch six
    /// times.</summary>
    private static async Task RunExportStepAsync(List<string> notes, string label, Func<Task<string?>> step)
    {
        try
        {
            string? failure = await step();
            if (failure is not null) notes.Add($"{label}: {failure}");
        }
        catch (Exception ex)
        {
            notes.Add($"{label}: {ex.Message}");
        }
    }

    private static int CopySelectedDumps(List<string> filePaths, string workDir, List<string> notes)
    {
        if (filePaths.Count == 0) return 0;
        string destDir = Path.Combine(workDir, "Dumps");
        Directory.CreateDirectory(destDir);

        int copied = 0;
        foreach (var path in filePaths)
        {
            try
            {
                if (!File.Exists(path)) { notes.Add($"Dump {Path.GetFileName(path)}: no longer on disk, skipped."); continue; }
                File.Copy(path, Path.Combine(destDir, Path.GetFileName(path)), overwrite: true);
                copied++;
            }
            catch (Exception ex)
            {
                notes.Add($"Dump {Path.GetFileName(path)}: couldn't copy it ({ex.Message}).");
            }
        }
        return copied;
    }

    private static int CopySelectedWerReports(List<string> reportFolders, string workDir, List<string> notes)
    {
        if (reportFolders.Count == 0) return 0;
        string destRoot = Path.Combine(workDir, "WerReports");
        Directory.CreateDirectory(destRoot);

        int copied = 0;
        foreach (var folder in reportFolders)
        {
            try
            {
                if (!Directory.Exists(folder)) { notes.Add($"WER report {Path.GetFileName(folder)}: no longer on disk, skipped."); continue; }
                string dest = Path.Combine(destRoot, Path.GetFileName(folder));
                Directory.CreateDirectory(dest);
                foreach (var file in Directory.GetFiles(folder))
                {
                    try { File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true); }
                    catch { /* one unreadable attached file (e.g. a locked heap snapshot) shouldn't stop the rest */ }
                }
                copied++;
            }
            catch (Exception ex)
            {
                notes.Add($"WER report {Path.GetFileName(folder)}: couldn't copy it ({ex.Message}).");
            }
        }
        return copied;
    }

    private static string BuildCrashDumpConfigText(CrashDumpConfiguration? cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Crash-dump configuration read-out");
        sb.AppendLine("==================================");
        sb.AppendLine($"Generated: {DateTime.Now:F}");
        sb.AppendLine();
        if (cfg is null)
        {
            sb.AppendLine("Not available - the crash-dump configuration hadn't been read yet when this bundle was built.");
            return sb.ToString();
        }
        foreach (var f in cfg.Fields) sb.AppendLine($"{f.Label}: {f.Value}");
        sb.AppendLine();
        sb.AppendLine($"Dump target: {cfg.DumpTargetPath ?? "Unknown"} ({cfg.DumpTargetHealthText})");
        return sb.ToString();
    }

    private static async Task<string> BuildMarkdownSummaryAsync(List<CrashCluster> clusters, CrashDumpConfiguration? cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Task Manager Plus — crash support summary");
        sb.AppendLine();
        sb.AppendLine($"- Generated: {DateTime.Now:F}");
        sb.AppendLine($"- Machine: {Environment.MachineName}");
        sb.AppendLine($"- OS build: {WerReportService.GetOsBuildString()}");
        sb.AppendLine();

        sb.AppendLine("## Will this PC capture the next crash?");
        if (cfg is not null)
        {
            var checklist = CrashDumpConfigService.BuildChecklist(cfg);
            sb.AppendLine(checklist.VerdictText);
        }
        else
        {
            sb.AppendLine("Not available.");
        }
        sb.AppendLine();

        sb.AppendLine("## Crash / fault clusters");
        sb.AppendLine();
        if (clusters.Count == 0)
        {
            sb.AppendLine("No recurring crash/fault clusters were found in the current lookback window.");
        }
        else
        {
            var ordered = clusters.OrderByDescending(c => c.Count).ThenByDescending(c => c.LastSeen).ToList();
            int whatChangedShown = 0;
            foreach (var c in ordered)
            {
                sb.AppendLine($"### {c.Title}");
                sb.AppendLine($"- {c.Description}");
                sb.AppendLine($"- First seen: {c.FirstSeen:g} · Last seen: {c.LastSeen:g}");
                sb.AppendLine($"- {c.CadenceText}");

                if (whatChangedShown < MaxClustersWithWhatChanged)
                {
                    whatChangedShown++;
                    try
                    {
                        var changed = await CrashCorrelationService.BuildWhatChangedAsync(c.FirstSeen);
                        sb.AppendLine("- What changed in the 48 hours before this started:");
                        if (!changed.ComputedOk)
                        {
                            sb.AppendLine($"  - Couldn't be determined ({changed.ErrorText}).");
                        }
                        else if (changed.Entries.Count == 0)
                        {
                            sb.AppendLine("  - Nothing found (no driver installs, Windows Updates, or application installs in that window).");
                        }
                        else
                        {
                            foreach (var e in changed.Entries)
                                sb.AppendLine($"  - {e.Timestamp:g} — [{e.Category}] {e.Description}");
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"- What changed: couldn't be determined ({ex.Message}).");
                    }
                }
                sb.AppendLine();
            }

            if (ordered.Count > MaxClustersWithWhatChanged)
                sb.AppendLine($"*(\"What changed\" is only shown above for the {MaxClustersWithWhatChanged} most significant clusters.)*");
        }

        return sb.ToString();
    }

    private static string Truncate(string s, int maxLen = 300) => string.IsNullOrEmpty(s) ? s : (s.Length <= maxLen ? s.Trim() : s[..maxLen].Trim() + "…");

    /// <summary>#1084: delegates to the shared <see cref="ToolRunner"/>, keeping this service's
    /// soft-start degradation (a tool that can't start yields an empty result, never a throw).</summary>
    private static async Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs)
    {
        try { return await ToolRunner.RunCapturedAsync(exe, args, timeoutMs); }
        catch { return (string.Empty, null); }
    }
}
