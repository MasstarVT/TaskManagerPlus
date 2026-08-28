using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #786/#787/#788: `sfc /scannow`, CBS.log's [SR]-line extraction, the persisted
/// integrity-history.json scan history (shared by both SFC and every DISM health-scan verb - see
/// AppendAndSave), and the in-place-repair-install guidance trigger. Grouped in one file per this
/// chunk's own instructions - they're one continuous "did the OS's own files check out, and what's
/// the escalation path if not" story rather than three independent features.
/// </summary>
public static class SfcIntegrityService
{
    private const string CbsLogPath = @"C:\Windows\Logs\CBS\CBS.log";
    private static string HistoryPath => AppPaths.GetPath("integrity-history.json");
    private const int MaxHistoryEntries = 100;

    private static readonly Regex ProgressPercentRegex = new(@"(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);
    private static readonly Regex SrLineRegex = new(@"\[SR\]", RegexOptions.Compiled);
    private static readonly Regex RepairedFileRegex = new(@"Repairing corrupted file \[[^\]]*\]\s*""(?<path>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex CannotRepairRegex = new(@"Cannot repair member file \[[^\]]*\]\s*""(?<file>[^""]+)""\s+of\s+(?<component>[^,]+)", RegexOptions.Compiled);

    private static readonly string[] KnownVerdictPhrases =
    {
        "Windows Resource Protection did not find any integrity violations.",
        "Windows Resource Protection found corrupt files and successfully repaired them.",
        "Windows Resource Protection found corrupt files but was unable to fix some of them.",
        "Windows Resource Protection could not perform the requested operation.",
    };

    #region #786 - sfc /scannow

    /// <summary>#786: runs `sfc /scannow` and, on top of its one-line verdict, parses
    /// %windir%\Logs\CBS\CBS.log for the [SR] lines this specific run appended - the actual
    /// repaired-file list and "Cannot repair member file" entries with component names, rather than
    /// just the verdict sentence. Progress parsed from sfc's own "Verification NN% complete."
    /// output.</summary>
    public static async Task<SfcScanResult> RunScanAsync(IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        long cbsStartOffset = SafeCbsLogLength();
        var sw = Stopwatch.StartNew();

        string output;
        int exitCode;
        try
        {
            (output, exitCode) = await RunSfcAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new SfcScanResult { Success = false, VerdictText = $"Couldn't run sfc /scannow: {ex.Message}" };
        }
        sw.Stop();

        var (repaired, unrepairable, extractPath) = ExtractCbsDelta(cbsStartOffset);
        string verdict = ExtractVerdict(output);
        bool hasUnrepairable = unrepairable.Count > 0 ||
            verdict.Contains("unable to fix", StringComparison.OrdinalIgnoreCase) ||
            verdict.Contains("was unable to repair", StringComparison.OrdinalIgnoreCase);
        bool foundViolations = hasUnrepairable || repaired.Count > 0 ||
            verdict.Contains("found corrupt files", StringComparison.OrdinalIgnoreCase);

        return new SfcScanResult
        {
            Success = exitCode == 0,
            VerdictText = verdict,
            FoundViolations = foundViolations,
            AllRepaired = foundViolations && !hasUnrepairable,
            RepairedFiles = repaired,
            UnrepairableEntries = unrepairable,
            DurationSeconds = sw.Elapsed.TotalSeconds,
            ExtractedLogPath = extractPath,
            RawOutputTail = TailLines(output, 12),
        };
    }

    /// <summary>
    /// Same concurrent-read/bounded-timeout shell-out shape as DismService.RunDismAsync (each
    /// shelling-out service owns its own small helper, per this app's established convention - see
    /// BootPerformanceService's own copy's remarks), adapted for sfc's percentage progress line.
    /// StandardOutputEncoding is explicitly set to Unicode: sfc.exe is one of the few console tools
    /// that writes UTF-16 to a redirected stdout pipe (a long-documented quirk - piping its output
    /// to a file with plain ANSI/OEM decoding produces text interleaved with null bytes), so without
    /// this every parse below would silently see garbage instead of the verdict/percentage text.
    /// </summary>
    private static async Task<(string Output, int ExitCode)> RunSfcAsync(IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("sfc.exe", "/scannow")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.Unicode,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start sfc.exe");

        var fullOutput = new StringBuilder();
        var errorTask = proc.StandardError.ReadToEndAsync();

        var readTask = Task.Run(async () =>
        {
            var buffer = new char[512];
            var chunk = new StringBuilder();
            int read;
            while ((read = await proc.StandardOutput.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    char c = buffer[i];
                    if (c is '\r' or '\n')
                    {
                        if (chunk.Length > 0)
                        {
                            string line = chunk.ToString();
                            fullOutput.AppendLine(line);
                            ReportProgress(line, progress);
                            chunk.Clear();
                        }
                    }
                    else
                    {
                        chunk.Append(c);
                    }
                }
            }
            if (chunk.Length > 0)
            {
                string line = chunk.ToString();
                fullOutput.AppendLine(line);
                ReportProgress(line, progress);
            }
        });

        // sfc /scannow on a full system scan can legitimately take 10-20+ minutes - generous
        // timeout, guarding only against a truly hung process, plus the caller's own
        // CancellationToken wired to a Cancel button.
        using var timeoutCts = new CancellationTokenSource(60 * 60 * 1000);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await Task.WhenAll(readTask, proc.WaitForExitAsync(linkedCts.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            string partial = fullOutput.ToString();
            return (partial.Length > 0 ? partial : "(sfc timed out or was cancelled)", -1);
        }

        string errText = await errorTask.ConfigureAwait(false);
        return (fullOutput.ToString() + errText, proc.ExitCode);
    }

    private static void ReportProgress(string line, IProgress<int>? progress)
    {
        if (progress is null) return;
        var m = ProgressPercentRegex.Match(line);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out double pct))
            progress.Report((int)Math.Clamp(pct, 0, 100));
    }

    private static long SafeCbsLogLength()
    {
        try { return File.Exists(CbsLogPath) ? new FileInfo(CbsLogPath).Length : 0; }
        catch { return 0; }
    }

    private const int MaxCbsReadBytes = 20 * 1024 * 1024; // generous for one scan's worth of [SR] activity
    private const int MaxCbsRows = 300;

    /// <summary>
    /// Reads only the portion of CBS.log appended since `startOffset` (captured right before sfc
    /// started) rather than the whole file - CBS.log routinely grows to ~100 MB over a machine's
    /// lifetime, and re-scanning all of it on every call would be exactly the kind of "heavier than
    /// a trivial read" cost CLAUDE.md's on-demand convention exists to avoid repeating. Adaptive
    /// [SR]-line regex parse (not a documented, versioned CBS.log schema), same "degrade gracefully,
    /// don't assume an unverified contract" tradeoff BootPerformanceService's event-field extraction
    /// already takes. The extracted [SR] subset (not the whole log) is written under
    /// AppPaths.SettingsDirectory per this item's spec, rather than left in the 100 MB log.
    /// </summary>
    private static (List<string> Repaired, List<string> Unrepairable, string? ExtractPath) ExtractCbsDelta(long startOffset)
    {
        var repaired = new List<string>();
        var unrepairable = new List<string>();
        var srLines = new List<string>();

        try
        {
            if (File.Exists(CbsLogPath))
            {
                using var stream = new FileStream(CbsLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long length = stream.Length;
                long readStart = Math.Max(startOffset, length - MaxCbsReadBytes);
                stream.Seek(readStart, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (!SrLineRegex.IsMatch(line)) continue;
                    srLines.Add(line);

                    var repairMatch = RepairedFileRegex.Match(line);
                    if (repairMatch.Success) { repaired.Add(repairMatch.Groups["path"].Value); continue; }

                    var cannotMatch = CannotRepairRegex.Match(line);
                    if (cannotMatch.Success)
                        unrepairable.Add($"{cannotMatch.Groups["file"].Value} (component: {cannotMatch.Groups["component"].Value.Trim()})");
                }
            }
        }
        catch
        {
            return (repaired, unrepairable, null); // CBS.log unreadable/locked - the sfc verdict text still stands on its own
        }

        string? extractPath = null;
        if (srLines.Count > 0)
        {
            try
            {
                string dir = AppPaths.GetPath("IntegrityScans");
                Directory.CreateDirectory(dir);
                extractPath = Path.Combine(dir, $"cbs-extract-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                File.WriteAllLines(extractPath, srLines);
            }
            catch
            {
                extractPath = null; // best-effort - the in-memory Repaired/Unrepairable lists below still work
            }
        }

        return (repaired.Take(MaxCbsRows).ToList(), unrepairable.Take(MaxCbsRows).ToList(), extractPath);
    }

    private static string ExtractVerdict(string output)
    {
        foreach (var phrase in KnownVerdictPhrases)
            if (output.Contains(phrase, StringComparison.OrdinalIgnoreCase)) return phrase;

        var lastLine = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return lastLine ?? "(sfc produced no readable output)";
    }

    private static string TailLines(string output, int count)
        => string.Join(Environment.NewLine, output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).TakeLast(count));

    #endregion

    #region #787 - Integrity scan history

    /// <summary>#787: fail-silent-to-empty JSON load, same pattern as
    /// BootPerformanceService.LoadHistory's boot-history.json.</summary>
    public static List<IntegrityHistoryEntry> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var json = File.ReadAllText(HistoryPath);
                var entries = JsonSerializer.Deserialize<List<IntegrityHistoryEntry>>(json);
                if (entries is not null) return entries;
            }
        }
        catch { /* corrupt/unreadable file - start a fresh history rather than blocking the tab */ }
        return new List<IntegrityHistoryEntry>();
    }

    /// <summary>#787: appends one entry (from either an SFC or a DISM health-scan run - see
    /// WindowsHealthViewModel's callers) and persists, capped to the most recent
    /// MaxHistoryEntries.</summary>
    public static List<IntegrityHistoryEntry> AppendAndSave(IntegrityHistoryEntry entry)
    {
        var history = LoadHistory();
        history.Add(entry);
        history = history.OrderBy(h => h.Timestamp).TakeLast(MaxHistoryEntries).ToList();
        try
        {
            string dir = Path.GetDirectoryName(HistoryPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(history));
        }
        catch { /* best-effort persistence, same as boot-history.json */ }
        return history;
    }

    /// <summary>#787: "is this the same corruption as last time, new, or resolved" - compares the
    /// most recent entry of a given scan type against the one immediately before it of the same
    /// type.</summary>
    public static string? CompareToPreviousRun(List<IntegrityHistoryEntry> history, string scanType)
    {
        var sameType = history.Where(h => h.ScanType == scanType).OrderBy(h => h.Timestamp).ToList();
        if (sameType.Count < 2) return null;

        var latest = sameType[^1];
        var previous = sameType[^2];
        var latestSet = new HashSet<string>(latest.UnrepairableFiles, StringComparer.OrdinalIgnoreCase);
        var previousSet = new HashSet<string>(previous.UnrepairableFiles, StringComparer.OrdinalIgnoreCase);

        if (latestSet.Count == 0 && previousSet.Count > 0)
            return $"Resolved - the {previousSet.Count} unrepairable file(s) from the previous {scanType} scan are gone now.";
        if (latestSet.Count == 0)
            return "No unrepairable files, same as the previous scan.";
        if (latestSet.SetEquals(previousSet))
            return $"Same {latestSet.Count} unrepairable file(s) as the previous {scanType} scan.";

        int newCount = latestSet.Except(previousSet, StringComparer.OrdinalIgnoreCase).Count();
        return newCount > 0
            ? $"{newCount} new unrepairable file(s) since the previous {scanType} scan."
            : $"{latestSet.Count} unrepairable file(s) - overlaps with the previous scan, but the set changed.";
    }

    #endregion

    #region #788 - In-place repair-install guidance

    /// <summary>#788: true once SFC has reported unrepairable files on the two most recent SFC
    /// scans in a row - the documented escalation point where an in-place repair install is the
    /// next reasonable step. Only the most recent two count (not "ever twice"), so an old,
    /// since-resolved failure doesn't keep nagging.</summary>
    public static bool HasRepeatedUnrepairableSfcFailures(List<IntegrityHistoryEntry> history)
    {
        var sfcRuns = history.Where(h => h.ScanType == "SFC").OrderByDescending(h => h.Timestamp).Take(2).ToList();
        return sfcRuns.Count == 2 && sfcRuns.All(h => h.UnrepairableFiles.Count > 0);
    }

    /// <summary>#788: builds the repair-install checklist, with the ISO spec pre-filled from the
    /// registry (ReadMatchingImageSpec) rather than left for the user to look up. Guidance only -
    /// never downloads/launches an OS installer.</summary>
    public static RepairInstallGuidance BuildGuidance(string triggerReason)
    {
        var (edition, build, displayVersion, language, architecture) = ReadMatchingImageSpec();
        return new RepairInstallGuidance
        {
            TriggerReason = triggerReason,
            EditionText = edition,
            BuildText = build,
            DisplayVersionText = displayVersion,
            LanguageText = language,
            ArchitectureText = architecture,
            ChecklistItems = new List<string>
            {
                "Free at least 20-25 GB of disk space - Setup keeps a full backup of the current installation in C:\\Windows.old so you can roll back.",
                $"Download an ISO that matches this PC exactly: {edition}, build {build} ({displayVersion}), {language}, {architecture} - a mismatched edition/build/language commonly forces a clean install instead of an in-place repair.",
                "Temporarily disable or uninstall third-party antivirus/security software - it commonly blocks or interferes with Setup.exe partway through the upgrade.",
                "Mount the matching ISO, open an elevated command prompt in its root folder, then run the command below.",
                "This app never downloads or launches an OS installer itself - this card is guidance only.",
            },
            SetupCommandLine = "setup.exe /auto upgrade",
        };
    }

    /// <summary>Reads the build/edition/language this PC is actually running, straight from the
    /// registry, so the repair-install guidance names the exact ISO to go get rather than leaving
    /// the user to guess. Same "read a documented registry value, degrade to Unknown on anything
    /// missing" tradeoff every other registry read in this app takes.</summary>
    private static (string Edition, string Build, string DisplayVersion, string Language, string Architecture) ReadMatchingImageSpec()
    {
        string edition = "Unknown", build = "Unknown", displayVersion = "Unknown";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                if (key.GetValue("EditionID") as string is { Length: > 0 } editionId) edition = editionId;

                string? currentBuild = key.GetValue("CurrentBuild") as string ?? key.GetValue("CurrentBuildNumber") as string;
                string? ubr = key.GetValue("UBR")?.ToString();
                if (!string.IsNullOrWhiteSpace(currentBuild))
                    build = !string.IsNullOrWhiteSpace(ubr) ? $"{currentBuild}.{ubr}" : currentBuild;

                if ((key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string) is { Length: > 0 } display)
                    displayVersion = display;
            }
        }
        catch { /* degrade to Unknown, never fabricated */ }

        string architecture = "Unknown";
        try { architecture = Environment.Is64BitOperatingSystem ? "64-bit (x64)" : "32-bit (x86)"; }
        catch { /* leave Unknown */ }

        string language = "Unknown";
        try
        {
            var culture = CultureInfo.InstalledUICulture;
            language = $"{culture.EnglishName} ({culture.Name})";
        }
        catch { /* leave Unknown */ }

        return (edition, build, displayVersion, language, architecture);
    }

    #endregion
}
