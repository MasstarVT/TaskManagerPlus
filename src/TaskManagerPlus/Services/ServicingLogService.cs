using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #175-183: "Servicing, setup and update log parsing" - CBS.log/DISM.log/setuperr.log/
/// setupact.log parsing, CbsPersist_*.log archive expansion (via expand.exe), WindowsUpdate.log
/// decoding (via `Get-WindowsUpdateLog`, since Windows 10 stopped logging WU activity as plain
/// text), a combined update-failure history (event logs + WMI), pending-reboot/pending-servicing
/// registry signals, the AppX/AppReadiness failure channels, and a small CBS-folder health stat.
///
/// None of these logs/tools publish a documented, version-stable schema (CBS.log and dism.log are
/// internal diagnostic dumps, not a supported log format; setupact.log/setuperr.log drift across
/// Windows Setup versions) - every parse here is defensive line/regex matching over the raw text,
/// same as EtwTraceService's own remarks about logman/tracerpt output, and every result keeps
/// enough raw context (source path, raw line text) that a user can go verify a parsed field
/// against the real file if it ever looks wrong on some Windows build this wasn't tested against.
/// Every file read here shares one convention: open with FileShare.ReadWrite | FileShare.Delete,
/// since CBS.log in particular is very often still open for write by the TrustedInstaller service
/// while this app wants to read it.
/// </summary>
public static class ServicingLogService
{
    private static string WinDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string CbsLogFolder => Path.Combine(WinDir, "Logs", "CBS");
    private static string CbsLogPath => Path.Combine(CbsLogFolder, "CBS.log");
    private static string DismLogPath => Path.Combine(WinDir, "Logs", "DISM", "dism.log");
    private static string PantherFolder => Path.Combine(WinDir, "Panther");

    private static readonly Regex LeadingTimestampRegex = new(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})", RegexOptions.Compiled);
    private static readonly Regex ErrorCodeRegex = new(@"0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);
    private static readonly Regex CannotRepairPathRegex = new(@"Cannot repair member file\s*\[[^\]]*\]\s*""?([^""\r\n]+)""?", RegexOptions.Compiled);
    private static readonly Regex VerifyingCountRegex = new(@"Verifying\s+(\d+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex DismLevelRegex = new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},\s*(Info|Warning|Error)\b", RegexOptions.Compiled);
    private static readonly Regex DismPrefixRegex = new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},\s*(?:Info|Warning|Error)\s*", RegexOptions.Compiled);
    private static readonly Regex DismHResultRegex = new(@"(?:hr|HRESULT)\s*[:=]\s*(0x[0-9A-Fa-f]{8})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex KbRegex = new(@"\bKB\d{6,7}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ==================== #175: CBS.log parser ====================

    /// <summary>Reads CBS.log on demand and keeps only the `[SR]` lines (the sfc /scannow engine's
    /// own repair block), "Cannot repair member file" targets, and any 0xNNNNNNNN error codes
    /// found in them - a filtered list rather than surfacing the whole (often 100+ MB) file, per
    /// #175's own instructions.</summary>
    public static async Task<CbsLogSummary> ParseCbsLogAsync(int maxSrLines = 1000, CancellationToken ct = default)
    {
        var result = new CbsLogSummary { LogPath = CbsLogPath };
        if (!File.Exists(CbsLogPath))
        {
            result.ErrorMessage = "CBS.log wasn't found (no servicing activity has been logged yet, or the Logs\\CBS folder is missing/inaccessible).";
            return result;
        }

        result.Exists = true;
        try { result.SizeBytes = new FileInfo(CbsLogPath).Length; } catch { /* cosmetic only */ }

        try
        {
            await Task.Run(() => ScanCbsLogFile(CbsLogPath, result, maxSrLines, ct), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Couldn't read CBS.log: {ex.Message}";
        }
        return result;
    }

    private static void ScanCbsLogFile(string path, CbsLogSummary result, int maxSrLines, CancellationToken ct)
    {
        var errorCodesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 65536);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            result.TotalLinesScanned++;

            if (!line.Contains("[SR]", StringComparison.Ordinal)) continue;

            bool isUnrepairable = line.Contains("Cannot repair member file", StringComparison.OrdinalIgnoreCase);
            if (isUnrepairable)
            {
                var m = CannotRepairPathRegex.Match(line);
                result.CannotRepairFiles.Add(m.Success ? m.Groups[1].Value.Trim() : "(path not parsed - see raw CBS.log)");
            }

            foreach (Match cm in ErrorCodeRegex.Matches(line))
                if (errorCodesSeen.Add(cm.Value)) result.ErrorCodes.Add(cm.Value);

            if (result.SrLines.Count < maxSrLines)
            {
                result.SrLines.Add(new CbsSrLine
                {
                    Timestamp = ParseLeadingTimestamp(line),
                    Text = line.Length > 500 ? line[..500] : line,
                    IsUnrepairable = isUnrepairable,
                });
            }
            else
            {
                result.Truncated = true;
            }
        }
    }

    private static DateTime? ParseLeadingTimestamp(string line)
    {
        var m = LeadingTimestampRegex.Match(line);
        if (!m.Success) return null;
        return DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
    }

    // ==================== #176: SFC result summary (+ CbsPersist_*.log archive expansion) ====================

    /// <summary>Summarizes the CBS `[SR]` block into "N files scanned, M corrupt, K repaired, list
    /// of unrepairable files." <see cref="SfcResultSummary.FilesScanned"/> is left null when no
    /// "Verifying N (...) components" line is found - CBS.log doesn't always log an explicit scan
    /// count (sfc.exe prints its live progress to the console, not to the log), so this is never
    /// guessed. If the live CBS.log has no `[SR]` activity at all (it rolls over at ~128 MB, so an
    /// older sfc run may have already scrolled out of it), this falls back to scanning
    /// CbsPersist_*.log archives newest-first - #176's "these are CAB-compressed .log files, expand
    /// via expand.exe rather than writing a CAB decoder" - stopping at the first archive that
    /// actually has `[SR]` activity, bounded to the 10 most recent archives so a machine with years
    /// of history doesn't expand every archive it has.</summary>
    public static async Task<SfcResultSummary> SummarizeSfcResultAsync(Action<string>? progress = null, CancellationToken ct = default)
    {
        var summary = new SfcResultSummary();

        if (File.Exists(CbsLogPath))
        {
            progress?.Invoke("Scanning CBS.log for [SR] activity...");
            var stats = await Task.Run(() => ScanForSfcStats(CbsLogPath, ct), ct);
            if (stats.HasSrActivity)
            {
                ApplyStats(summary, stats, CbsLogPath);
                return summary;
            }
        }

        var archives = ListCbsPersistArchives();
        if (archives.Count == 0)
        {
            summary.Found = false;
            summary.ErrorMessage = "No [SR] (System File Checker) activity found in CBS.log, and no CbsPersist_*.log archives exist to fall back to.";
            return summary;
        }

        string? tempExpanded = null;
        try
        {
            int checkedCount = 0;
            foreach (var archive in archives.Take(10))
            {
                ct.ThrowIfCancellationRequested();
                checkedCount++;
                progress?.Invoke($"No [SR] activity in the live log - checking archive {checkedCount}/{Math.Min(archives.Count, 10)}: {Path.GetFileName(archive)}...");

                string? expandedPath = await ExpandCbsPersistArchiveAsync(archive, ct);
                if (expandedPath is null) continue;
                tempExpanded = expandedPath;

                var stats = await Task.Run(() => ScanForSfcStats(expandedPath, ct), ct);
                if (stats.HasSrActivity)
                {
                    ApplyStats(summary, stats, archive);
                    return summary;
                }

                try { File.Delete(expandedPath); } catch { /* best-effort cleanup */ }
                tempExpanded = null;
            }

            summary.Found = false;
            summary.ErrorMessage = $"No [SR] activity found in CBS.log or in the {Math.Min(archives.Count, 10)} most recent CbsPersist_*.log archive(s) checked.";
            return summary;
        }
        finally
        {
            if (tempExpanded is not null) { try { File.Delete(tempExpanded); } catch { } }
        }
    }

    private readonly struct SfcScanStats
    {
        public bool HasSrActivity { get; init; }
        public int? FilesScanned { get; init; }
        public int RepairedCount { get; init; }
        public List<string> UnrepairableFiles { get; init; }
        public DateTime? LastActivityUtc { get; init; }
    }

    private static SfcScanStats ScanForSfcStats(string path, CancellationToken ct)
    {
        bool hasActivity = false;
        int? filesScanned = null;
        int repaired = 0;
        var unrepairable = new List<string>();
        DateTime? lastActivity = null;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 65536);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (!line.Contains("[SR]", StringComparison.Ordinal)) continue;
            hasActivity = true;

            var ts = ParseLeadingTimestamp(line);
            if (ts is not null) lastActivity = ts;

            var vm = VerifyingCountRegex.Match(line);
            if (vm.Success && int.TryParse(vm.Groups[1].Value, out int n)) filesScanned = n;

            if (line.Contains("Cannot repair member file", StringComparison.OrdinalIgnoreCase))
            {
                var m = CannotRepairPathRegex.Match(line);
                unrepairable.Add(m.Success ? m.Groups[1].Value.Trim() : "(path not parsed - see raw CBS.log)");
            }
            else if (line.Contains("Repairing corrupted file", StringComparison.OrdinalIgnoreCase))
            {
                repaired++;
            }
        }

        return new SfcScanStats
        {
            HasSrActivity = hasActivity,
            FilesScanned = filesScanned,
            RepairedCount = repaired,
            UnrepairableFiles = unrepairable,
            LastActivityUtc = lastActivity,
        };
    }

    private static void ApplyStats(SfcResultSummary summary, SfcScanStats stats, string sourcePath)
    {
        summary.Found = true;
        summary.SourceLogs.Add(sourcePath);
        summary.FilesScanned = stats.FilesScanned;
        summary.RepairedCount = stats.RepairedCount;
        summary.UnrepairableFiles = stats.UnrepairableFiles;
        summary.CorruptCount = stats.RepairedCount + stats.UnrepairableFiles.Count;
        summary.LastSrActivityUtc = stats.LastActivityUtc;
    }

    private static List<string> ListCbsPersistArchives()
    {
        try
        {
            if (!Directory.Exists(CbsLogFolder)) return new List<string>();
            return Directory.GetFiles(CbsLogFolder, "CbsPersist_*.log")
                .OrderByDescending(f => { try { return File.GetLastWriteTimeUtc(f); } catch { return DateTime.MinValue; } })
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>CbsPersist_*.log files are renamed single-file CAB archives, not plain text -
    /// expand.exe (a built-in Windows tool) decompresses them without this app needing to write a
    /// CAB decoder, per #176's own instructions. Returns null (never throws) on any failure -
    /// missing expand.exe, a corrupt archive, access denied - so the caller's archive-scanning loop
    /// just moves on to the next one.</summary>
    private static async Task<string?> ExpandCbsPersistArchiveAsync(string archivePath, CancellationToken ct)
    {
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "TaskManagerPlus-CbsPersist");
            Directory.CreateDirectory(tempDir);
            string destPath = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(archivePath)}-{Guid.NewGuid():N}.log");

            await RunCapturedAsync("expand.exe", $"\"{archivePath}\" \"{destPath}\"", 30000, ct);
            // expand.exe's own exit code carries the same "don't fully trust it" caveat every other
            // shelled-out tool in this app does - check the output file actually landed and has
            // content instead of gating on ExitCode == 0.
            return File.Exists(destPath) && new FileInfo(destPath).Length > 0 ? destPath : null;
        }
        catch
        {
            return null;
        }
    }

    // ==================== #177: DISM.log parser ====================

    /// <summary>Parses %WinDir%\Logs\DISM\dism.log for the Error/Warning lines of the most recent
    /// session only - dism.log accumulates across every DISM invocation ever made on the machine
    /// (it is never rotated/truncated), so this streams the file keeping a bounded ring buffer of
    /// only the last ~8000 lines rather than holding the whole thing in memory, then finds the most
    /// recent "session" by scanning backward for the first timestamp gap over 2 minutes - DISM
    /// doesn't write an explicit session delimiter, but each invocation's own burst of lines is
    /// written back-to-back with no gap, so a multi-minute gap reliably means "a different DISM
    /// run." Each Error/Warning line's HRESULT (if any) is left for the caller to decode via #124's
    /// StatusCodeResolverService - reused, not duplicated.</summary>
    public static async Task<DismLogSummary> ParseDismLogAsync(CancellationToken ct = default)
    {
        var result = new DismLogSummary { LogPath = DismLogPath };
        if (!File.Exists(DismLogPath))
        {
            result.ErrorMessage = "dism.log wasn't found (DISM hasn't been run on this machine, or the Logs\\DISM folder is missing/inaccessible).";
            return result;
        }
        result.Exists = true;

        try
        {
            await Task.Run(() => ScanDismLogFile(result, ct), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Couldn't read dism.log: {ex.Message}";
        }
        return result;
    }

    private static void ScanDismLogFile(DismLogSummary result, CancellationToken ct)
    {
        const int ringCapacity = 8000;
        var ring = new LinkedList<(DateTime? Timestamp, string Line)>();

        using (var stream = new FileStream(DismLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
        using (var reader = new StreamReader(stream, Encoding.UTF8, true, 65536))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                ct.ThrowIfCancellationRequested();
                ring.AddLast((ParseLeadingTimestamp(line), line));
                if (ring.Count > ringCapacity) ring.RemoveFirst();
            }
        }

        if (ring.Count == 0) return;
        var items = ring.ToList();

        int sessionStartIndex = 0;
        DateTime? prevTs = null;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var ts = items[i].Timestamp;
            if (ts is not null && prevTs is not null && (prevTs.Value - ts.Value) > TimeSpan.FromMinutes(2))
            {
                sessionStartIndex = i + 1;
                break;
            }
            if (ts is not null) prevTs = ts;
        }

        var session = items.Skip(sessionStartIndex).ToList();
        result.LinesScannedInSession = session.Count;
        result.SessionStartUtc = session.Select(s => s.Timestamp).FirstOrDefault(t => t is not null);
        result.SessionEndUtc = session.Select(s => s.Timestamp).LastOrDefault(t => t is not null);

        foreach (var (ts, text) in session)
        {
            var lm = DismLevelRegex.Match(text);
            if (!lm.Success) continue;
            string level = lm.Groups[1].Value;
            if (!level.Equals("Error", StringComparison.OrdinalIgnoreCase) && !level.Equals("Warning", StringComparison.OrdinalIgnoreCase)) continue;

            var hm = DismHResultRegex.Match(text);
            string operation = DismPrefixRegex.Replace(text, "").Trim();

            result.Entries.Add(new DismLogEntry
            {
                Timestamp = ts,
                Level = level,
                Operation = operation.Length > 300 ? operation[..300] : operation,
                HResultCode = hm.Success ? hm.Groups[1].Value : null,
                RawLine = text.Length > 500 ? text[..500] : text,
            });
        }
    }

    // ==================== #178: upgrade/setup failure analysis ====================

    /// <summary>Reads %WinDir%\Panther\setuperr.log/setupact.log (or, when present,
    /// $WINDOWS.~BT\Sources\Panther on the system drive - left behind only by an in-place upgrade
    /// that actually rolled back, which is why it's preferred over %WinDir%\Panther when both
    /// exist: it's the more specific answer to "why did setup fail" instead of the log folder every
    /// successful upgrade also writes to) for the rollback reason (setuperr.log, which by
    /// definition only contains errors) and the last operation attempted before failure
    /// (setupact.log's tail - this file can run to hundreds of MB on a busy upgrade, so only its
    /// tail is ever read, never the whole file).</summary>
    public static async Task<SetupFailureAnalysis> AnalyzeSetupFailureAsync(CancellationToken ct = default)
    {
        string sysDrive = Path.GetPathRoot(WinDir) is { Length: > 0 } root ? root : @"C:\";
        string btPanther = Path.Combine(sysDrive, "$WINDOWS.~BT", "Sources", "Panther");

        bool btHasLogs = Directory.Exists(btPanther)
            && (File.Exists(Path.Combine(btPanther, "setuperr.log")) || File.Exists(Path.Combine(btPanther, "setupact.log")));

        string folder = btHasLogs ? btPanther : PantherFolder;
        var result = new SetupFailureAnalysis { SourceFolder = folder, IsFromFailedUpgradeLeftovers = btHasLogs };

        string errPath = Path.Combine(folder, "setuperr.log");
        string actPath = Path.Combine(folder, "setupact.log");

        if (!File.Exists(errPath) && !File.Exists(actPath))
        {
            result.ErrorMessage = $"Neither setuperr.log nor setupact.log was found under \"{folder}\".";
            return result;
        }

        result.Found = true;

        if (File.Exists(errPath))
        {
            try
            {
                result.SetupErrLastWriteUtc = File.GetLastWriteTimeUtc(errPath);
                result.RollbackReasonLines = await Task.Run(() => ReadNonBlankTail(errPath, 400_000, 200, ct), ct);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"Couldn't read setuperr.log: {ex.Message}";
            }
        }

        if (File.Exists(actPath))
        {
            try
            {
                result.SetupActLastWriteUtc = File.GetLastWriteTimeUtc(actPath);
                result.LastOperationLines = await Task.Run(() => ReadNonBlankTail(actPath, 400_000, 60, ct), ct);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = (result.ErrorMessage is null ? "" : result.ErrorMessage + " ") + $"Couldn't read setupact.log: {ex.Message}";
            }
        }

        return result;
    }

    /// <summary>Reads only the last <paramref name="tailBytes"/> of a (potentially huge) text log
    /// and returns its last <paramref name="maxLines"/> non-blank lines - never a full-file read.</summary>
    private static List<string> ReadNonBlankTail(string path, int tailBytes, int maxLines, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536);
        long length = stream.Length;
        long start = Math.Max(0, length - tailBytes);
        stream.Seek(start, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.UTF8, true, 65536);
        string text = reader.ReadToEnd();
        ct.ThrowIfCancellationRequested();

        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.Count <= maxLines ? lines : lines.Skip(lines.Count - maxLines).ToList();
    }

    // ==================== #179: WindowsUpdate.log on demand (Get-WindowsUpdateLog) ====================

    /// <summary>Since Windows 10, Windows Update no longer logs to plain text - the real activity
    /// lives in ETL traces under %WinDir%\Logs\WindowsUpdate that only `Get-WindowsUpdateLog`
    /// (which decodes them via tracerpt under the hood) can turn back into readable text, per
    /// #179's own instructions. This routinely takes tens of seconds to a couple of minutes
    /// depending on how much ETL history exists, hence the generous timeout - callers must run this
    /// via Task.Run/await (never on the UI thread) and show a busy/progress indicator, which
    /// ServicingLogsViewModel does via its own IsWuLogRunning flag.</summary>
    public static async Task<WindowsUpdateLogResult> RunGetWindowsUpdateLogAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new WindowsUpdateLogResult();
        string outPath = AppPaths.GetPath("WindowsUpdate.log");

        try
        {
            try { Directory.CreateDirectory(AppPaths.SettingsDirectory); } catch { /* Get-WindowsUpdateLog itself will surface any real problem */ }

            string escapedPath = outPath.Replace("'", "''");
            string psCommand = $"Get-WindowsUpdateLog -LogPath '{escapedPath}'";
            var (output, _) = await RunCapturedAsync("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{psCommand}\"", 180_000, ct);

            if (!File.Exists(outPath))
            {
                result.Success = false;
                result.ErrorMessage = string.IsNullOrWhiteSpace(output)
                    ? "Get-WindowsUpdateLog didn't produce a log file, and printed no output."
                    : $"Get-WindowsUpdateLog didn't produce a log file. Output: {Truncate(output, 500)}";
                return result;
            }

            result.Success = true;
            result.LogFilePath = outPath;
            result.Failures = await Task.Run(() => ScanWindowsUpdateLogFailures(outPath, ct), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Couldn't run Get-WindowsUpdateLog: {ex.Message}";
        }
        finally
        {
            sw.Stop();
            result.Duration = sw.Elapsed;
        }
        return result;
    }

    private static List<WindowsUpdateLogFailureLine> ScanWindowsUpdateLogFailures(string path, CancellationToken ct)
    {
        var failures = new List<WindowsUpdateLogFailureLine>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 65536);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (failures.Count >= 500) break;
            if (line.Contains("FAILED", StringComparison.Ordinal) || ErrorCodeRegex.IsMatch(line))
                failures.Add(new WindowsUpdateLogFailureLine { Text = line.Length > 500 ? line[..500] : line });
        }
        return failures;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    // ==================== #180: combined update failure history ====================

    /// <summary>#180: a small hand-written table of well-known servicing/update result codes, kept
    /// separate from #124's general StatusCodeResolverService (which shells out to certutil per
    /// code and is async) - these three codes are common and specific enough in update-failure
    /// history that a synchronous local lookup is worth having so this list renders instantly.
    /// Anything not in this table is still resolved via StatusCodeResolverService as a fallback by
    /// ServicingLogsViewModel, so nothing here is lost, just answered faster for the common cases.</summary>
    private static readonly Dictionary<string, string> KnownServicingCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0x800f081f"] = "The source files needed for the operation couldn't be found (CBS_E_SOURCE_MISSING) - often the online Windows Update source, an ISO, or a DISM /Source path wasn't reachable or didn't have a matching version.",
        ["0x80073712"] = "A file the update needs is missing or corrupted (the component store is damaged) - worth trying DISM /Online /Cleanup-Image /RestoreHealth followed by sfc /scannow.",
        ["0x800f0922"] = "The update couldn't be installed - commonly caused by too little free space on the system-reserved partition, or the machine couldn't reach the servicing endpoint it needed.",
    };

    /// <summary>Combines Microsoft-Windows-WindowsUpdateClient/Operational events 19
    /// (success)/20 (failure), the Setup channel's events 1-4, and Win32_QuickFixEngineering (WMI,
    /// installed-hotfix inventory only - no failure info by definition) into one "update history
    /// with reasons" list, sorted newest first. Reuses EventLogExplorerService.ReadPage for both
    /// event-log reads (#103's existing paged-read capability) rather than adding a fourth
    /// event-log reading path, per #180's own instructions.</summary>
    public static async Task<List<UpdateHistoryEntry>> LoadUpdateHistoryAsync(CancellationToken ct = default)
    {
        var entries = new List<UpdateHistoryEntry>();
        var eventLog = new EventLogExplorerService();

        await Task.Run(() =>
        {
            try
            {
                var page = eventLog.ReadPage("Microsoft-Windows-WindowsUpdateClient/Operational", "*[System[(EventID=19 or EventID=20)]]", null, pageSize: 300);
                if (page.ErrorText is null)
                    foreach (var row in page.Rows) entries.Add(BuildWuClientEntry(row));
            }
            catch { /* channel missing/inaccessible on this build - no rows from this source, not an error */ }
        }, ct);

        await Task.Run(() =>
        {
            try
            {
                var page = eventLog.ReadPage("Setup", "*[System[(EventID=1 or EventID=2 or EventID=3 or EventID=4)]]", null, pageSize: 300);
                if (page.ErrorText is null)
                    foreach (var row in page.Rows) entries.Add(BuildSetupChannelEntry(row));
            }
            catch { }
        }, ct);

        await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering");
                foreach (ManagementObject mo in searcher.Get())
                {
                    entries.Add(new UpdateHistoryEntry
                    {
                        TimeCreated = ParseQfeInstalledOn(mo["InstalledOn"]),
                        Source = "QFE inventory",
                        Success = true,
                        KbArticle = mo["HotFixID"] as string,
                        Description = mo["Description"] as string ?? "Installed update",
                    });
                }
            }
            catch { /* WMI namespace/provider unavailable - no rows from this source, not an error */ }
        }, ct);

        return entries.OrderByDescending(e => e.TimeCreated ?? DateTime.MinValue).ToList();
    }

    private static DateTime? ParseQfeInstalledOn(object? raw)
    {
        // Win32_QuickFixEngineering.InstalledOn is declared as a WMI datetime but is commonly
        // returned as a plain locale-formatted date string rather than the DMTF datetime format -
        // a long-documented WMI quirk - so both shapes are tried before giving up.
        if (raw is DateTime dt) return dt;
        if (raw is string s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return parsed;
        return null;
    }

    private static UpdateHistoryEntry BuildWuClientEntry(EventRecordRow row)
    {
        var codeMatch = ErrorCodeRegex.Match(row.Message);
        var kbMatch = KbRegex.Match(row.Message);
        var entry = new UpdateHistoryEntry
        {
            TimeCreated = row.TimeCreated,
            Source = "Windows Update Client",
            Success = row.EventId == 19,
            KbArticle = kbMatch.Success ? kbMatch.Value : null,
            ResultCode = codeMatch.Success ? codeMatch.Value : null,
            Description = FirstLine(row.Message),
        };
        ApplyKnownServicingCode(entry);
        return entry;
    }

    private static UpdateHistoryEntry BuildSetupChannelEntry(EventRecordRow row)
    {
        var codeMatch = ErrorCodeRegex.Match(row.Message);
        var kbMatch = KbRegex.Match(row.Message);
        // The Setup channel's events 1-4 aren't documented to each mean a specific
        // success/failure outcome - only an Error-level row is treated as a confirmed failure;
        // everything else is left Unknown (null) rather than guessed, per "quick flag, not a
        // verdict."
        bool? success = row.Level.Equals("Error", StringComparison.OrdinalIgnoreCase) ? false : null;

        var entry = new UpdateHistoryEntry
        {
            TimeCreated = row.TimeCreated,
            Source = "Setup",
            Success = success,
            KbArticle = kbMatch.Success ? kbMatch.Value : null,
            ResultCode = codeMatch.Success ? codeMatch.Value : null,
            Description = $"Event {row.EventId}: {FirstLine(row.Message)}",
        };
        ApplyKnownServicingCode(entry);
        return entry;
    }

    private static void ApplyKnownServicingCode(UpdateHistoryEntry entry)
    {
        if (entry.ResultCode is not null && KnownServicingCodes.TryGetValue(entry.ResultCode, out var meaning))
            entry.ResultCodeMeaning = meaning;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no message)";
        string first = text.Replace("\r\n", "\n").Split('\n')[0].Trim();
        return first.Length > 300 ? first[..300] : first;
    }

    // ==================== #181: stuck-servicing detector ====================

    /// <summary>Checks the pending-reboot/pending-servicing registry signals - each an
    /// independent read that degrades to "not present" (never an error) when its key/value doesn't
    /// exist on this Windows build, per #181's own "check the CBS registry key structure, degrade
    /// cleanly if a given signal's key doesn't exist" instruction.</summary>
    public static PendingServicingStatus CheckPendingServicing()
    {
        const string cbsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing";
        return new PendingServicingStatus
        {
            CheckedAtUtc = DateTime.UtcNow,
            CbsRebootPending = SubKeyExists($@"{cbsRoot}\RebootPending"),
            CbsRebootInProgress = SubKeyExists($@"{cbsRoot}\RebootInProgress"),
            CbsSessionsPending = SubKeyExists($@"{cbsRoot}\SessionsPending"),
            CbsPackagesPendingCount = CountSubkeyEntries($@"{cbsRoot}\PackagesPending"),
            WindowsUpdateRebootRequired = SubKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"),
            PendingFileRenameOperations = ValueExists(@"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations"),
        };
    }

    private static bool SubKeyExists(string path)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key is not null;
        }
        catch
        {
            return false; // denied/missing - treated the same as genuinely absent, never thrown
        }
    }

    private static int CountSubkeyEntries(string path)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is null) return 0;
            // PackagesPending's exact shape (subkeys vs. values) has varied across Windows
            // versions - count whichever this build actually populated.
            return Math.Max(key.GetSubKeyNames().Length, key.GetValueNames().Length);
        }
        catch
        {
            return 0;
        }
    }

    private static bool ValueExists(string path, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(valueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    // ==================== #182: App/Store install failure channel ====================

    /// <summary>Surfaces Microsoft-Windows-AppXDeployment-Server/Operational and
    /// Microsoft-Windows-AppReadiness/Operational Error/Warning events - reuses
    /// EventLogExplorerService.ReadPage (#103) again, scoped to these two channels, rather than a
    /// third bespoke event-log reading path.</summary>
    public static async Task<List<EventRecordRow>> LoadAppxFailuresAsync(CancellationToken ct = default)
    {
        var results = new List<EventRecordRow>();
        var eventLog = new EventLogExplorerService();
        string[] channels = { "Microsoft-Windows-AppXDeployment-Server/Operational", "Microsoft-Windows-AppReadiness/Operational" };
        const string xpath = "*[System[(Level=2 or Level=3)]]"; // 2 = Error, 3 = Warning

        foreach (var channel in channels)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                try
                {
                    var page = eventLog.ReadPage(channel, xpath, null, pageSize: 300);
                    if (page.ErrorText is null) results.AddRange(page.Rows);
                }
                catch { /* channel not present/accessible on this build - no rows from this channel */ }
            }, ct);
        }

        return results.OrderByDescending(r => r.TimeCreated).ToList();
    }

    // ==================== #183: servicing log health ====================

    /// <summary>The size of %WinDir%\Logs\CBS and how many CbsPersist_*.log archives it holds - a
    /// small stat, not a health verdict; paired in the UI with a reveal-in-Explorer button that
    /// reuses EtwTraceService.RevealInExplorer (#159's helper) rather than a second copy.</summary>
    public static CbsLogHealth GetCbsLogHealth()
    {
        var health = new CbsLogHealth { FolderPath = CbsLogFolder };
        try
        {
            if (!Directory.Exists(CbsLogFolder))
            {
                health.ErrorMessage = "The Logs\\CBS folder doesn't exist.";
                return health;
            }
            health.Exists = true;

            long total = 0;
            int archiveCount = 0;
            foreach (var file in Directory.EnumerateFiles(CbsLogFolder, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var info = new FileInfo(file);
                    total += info.Length;
                    if (info.Name.StartsWith("CbsPersist_", StringComparison.OrdinalIgnoreCase)) archiveCount++;
                }
                catch { /* one unreadable file shouldn't drop the rest of the tally */ }
            }
            health.FolderSizeBytes = total;
            health.CbsPersistArchiveCount = archiveCount;
        }
        catch (Exception ex)
        {
            health.ErrorMessage = $"Couldn't read the CBS logs folder: {ex.Message}";
        }
        return health;
    }

    // ==================== shared process runner ====================

    /// <summary>Shells out and captures combined stdout+stderr, bounded by a real timeout - the
    /// same concurrent-read/bounded-wait/kill-on-timeout pattern EtwTraceService.RunCapturedAsync
    /// and StatusCodeResolverService's certutil call already establish. A timed-out run returns
    /// ExitCode: null rather than throwing.</summary>
    private static async Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs, CancellationToken ct = default)
    {
        var result = await ToolRunner.RunCapturedAsync(exe, args, timeoutMs, ct);
        // External cancellation rethrows (callers drop the work); a plain timeout returns the sentinel.
        if (result.ExitCode is null) ct.ThrowIfCancellationRequested();
        return result;
    }
}
