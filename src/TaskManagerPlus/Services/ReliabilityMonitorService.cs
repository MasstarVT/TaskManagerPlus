using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.Json;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #169-174: Reliability Monitor data - Windows' own hourly SystemStabilityIndex
/// (Win32_ReliabilityStabilityMetrics, #169), the full application/Windows/miscellaneous-failure,
/// warning, and informational feed (Win32_ReliabilityRecords, #170), an on-demand re-aggregation
/// via the RAC scheduled task (#171), the WMIEnable disabled-collection check and its re-enable
/// write (#172), and a small correlation helper that flags a software-change record shortly before
/// a crash cluster from the unified incident timeline (#173). Both WMI classes are queried the same
/// try/catch-degrades-to-empty, no-namespace-argument (root\cimv2, the default) way every other
/// Win32_* read in this app already uses - see StorageSpacesService/SystemSpecsService for the
/// established shape this mirrors. Every read here degrades to empty/Unknown/null on failure rather
/// than throwing or fabricating a value - a missing WMI class (Reliability Analysis Component isn't
/// present on every Windows edition), a denied registry key, or a missing scheduled task are all
/// real, expected conditions, the same "degrade, never fabricate" rule CLAUDE.md documents. The one
/// write path (#172's re-enable) is never called from here on its own - StabilityViewModel gates it
/// behind an explicit MessageBox confirmation first, mirroring WerReportService's LocalDumps
/// toggle (#165) exactly, backup-before-write and one-click revert included.
/// </summary>
public sealed class ReliabilityMonitorService
{
    private const int LookbackDays = 30;
    private const string ReliabilityAnalysisPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Reliability Analysis";
    private const string ReliabilityAnalysisWmiPath = ReliabilityAnalysisPath + @"\WMI";

    // ==== #169: Windows' own per-hour stability index ====

    /// <summary>Reads every Win32_ReliabilityStabilityMetrics row within <paramref
    /// name="lookbackDays"/>, newest first. There is no convenient WQL WHERE clause for
    /// TimeGenerated (it's CIM_DATETIME, not a plain comparable string) so - the same tradeoff
    /// SystemSpecsService.ReadRecentHotfixes already takes for Win32_QuickFixEngineering - this
    /// reads every row and filters/sorts in C#; RAC's own retention already keeps this class small
    /// (~30 days x ~24 samples/day, verified live while building this).</summary>
    public List<ReliabilityStabilitySample> ReadStabilityMetrics(int lookbackDays = LookbackDays)
    {
        var results = new List<ReliabilityStabilitySample>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TimeGenerated, SystemStabilityIndex FROM Win32_ReliabilityStabilityMetrics");
            foreach (ManagementObject mo in searcher.Get())
            {
                DateTime when;
                try { when = ManagementDateTimeConverter.ToDateTime((string)mo["TimeGenerated"]); }
                catch { continue; } // unparseable timestamp - skip this one sample rather than guess

                double index;
                try { index = Convert.ToDouble(mo["SystemStabilityIndex"] ?? 0.0); }
                catch { continue; }

                results.Add(new ReliabilityStabilitySample { TimeGenerated = when, SystemStabilityIndex = index });
            }
        }
        catch
        {
            // Win32_ReliabilityStabilityMetrics unavailable (namespace/class missing on this
            // Windows edition, or WMI repository issue) - degrade to empty, same as
            // StorageSpacesService.List when MSFT_VirtualDisk isn't there.
        }

        var cutoff = DateTime.Now.AddDays(-lookbackDays);
        return results.Where(s => s.TimeGenerated >= cutoff).OrderByDescending(s => s.TimeGenerated).ToList();
    }

    /// <summary>#169: folds the hourly samples above down to one value per calendar day - the last
    /// sample of each day (closest to "today's current reading", the same value Reliability
    /// Monitor's own per-day graph point reflects) - zero-filled days would misrepresent "no data"
    /// as "index 0", so a day with no sample at all is left null (a real gap in the overlay line,
    /// not a fabricated low score) rather than defaulting to 0 the way DailyEventCounts safely can
    /// for an event count. Aligned to the exact same [today-(lookbackDays-1), today] window
    /// EventLogService.BuildDailyCounts uses, oldest first, so the two series can share one X
    /// axis.</summary>
    public static List<double?> BuildDailyIndex(IEnumerable<ReliabilityStabilitySample> samples, int lookbackDays = LookbackDays)
    {
        var lastPerDay = samples
            .GroupBy(s => s.TimeGenerated.Date)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.TimeGenerated).First().SystemStabilityIndex);

        var result = new List<double?>();
        var today = DateTime.Now.Date;
        for (int i = lookbackDays - 1; i >= 0; i--)
        {
            var day = today.AddDays(-i);
            result.Add(lastPerDay.TryGetValue(day, out var v) ? v : (double?)null);
        }
        return result;
    }

    // ==== #170: the full reliability record feed ====

    /// <summary>Reads every Win32_ReliabilityRecords row within <paramref name="lookbackDays"/>,
    /// newest first - application failures, Windows failures, miscellaneous failures, warnings, and
    /// informational entries (software installs/updates/uninstalls) all come through this one WMI
    /// class. Same "read everything, filter/sort in C#" tradeoff as ReadStabilityMetrics above.</summary>
    public List<ReliabilityRecordInfo> ReadRecords(int lookbackDays = LookbackDays)
    {
        var results = new List<ReliabilityRecordInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TimeGenerated, SourceName, EventIdentifier, ProductName, Message FROM Win32_ReliabilityRecords");
            foreach (ManagementObject mo in searcher.Get())
            {
                DateTime when;
                try { when = ManagementDateTimeConverter.ToDateTime((string)mo["TimeGenerated"]); }
                catch { continue; }

                string sourceName = (mo["SourceName"] as string ?? string.Empty).Trim();
                string message = (mo["Message"] as string ?? string.Empty).Trim();
                int eventId = 0;
                try { eventId = Convert.ToInt32(mo["EventIdentifier"] ?? 0); } catch { /* leave 0 */ }

                results.Add(new ReliabilityRecordInfo
                {
                    TimeGenerated = when,
                    SourceName = sourceName,
                    EventIdentifier = eventId,
                    ProductName = (mo["ProductName"] as string ?? string.Empty).Trim(),
                    Message = message,
                    Category = Classify(sourceName, eventId, message),
                });
            }
        }
        catch
        {
            // Win32_ReliabilityRecords unavailable - degrade to empty, same as ReadStabilityMetrics.
        }

        var cutoff = DateTime.Now.AddDays(-lookbackDays);
        return results.Where(r => r.TimeGenerated >= cutoff).OrderByDescending(r => r.TimeGenerated).ToList();
    }

    /// <summary>Best-effort bucketing of one record into Informational/Warning/Failure/Other -
    /// Win32_ReliabilityRecords itself carries no severity field, only SourceName+EventIdentifier
    /// (effectively "which provider logged this, and its own event ID"), so this is the same
    /// "several known shapes tried first, then a keyword fallback over the tool's own rendered text,
    /// degrade to Other rather than guess" tier as EventTimelineService.CategorizeChange and
    /// ClassifyBootType - "quick flag, not a verdict" (CLAUDE.md). Known shapes, all verified live
    /// against a real machine's actual Win32_ReliabilityRecords rows while building this:
    ///  - Microsoft-Windows-WindowsUpdateClient 19/23 ("Installation/Uninstallation Successful") ->
    ///    Informational; 20/24 ("...Failure") -> Failure. Any other WindowsUpdateClient event ID
    ///    falls through to the message-text keyword check below rather than a guessed ID meaning.
    ///  - MsiInstaller (1033 install / 1034 remove / 1035 reconfigure, or any other MsiInstaller
    ///    event) -> Informational - a completed install/removal/reconfiguration is itself the
    ///    software-change signal #170/#173 care about, regardless of the trailing "success or error
    ///    status" code MSI appends to its own message.
    ///  - "Application Hang" -> Warning (matches WerHangInfo's own "went white and unresponsive" is
    ///    a different thing from "disappeared" distinction already made elsewhere on this tab).
    ///  - "Application Error" / "Windows Error Reporting" -> Failure.
    /// Anything else: a keyword scan over SourceName+Message for install/update/uninstall wording
    /// (Informational, unless it also mentions failure/error wording) or failure/crash/hang/blue-
    /// screen wording (Failure/Warning) - Other when nothing matches, always still shown, never
    /// hidden.</summary>
    internal static ReliabilityRecordCategory Classify(string sourceName, int eventId, string message)
    {
        if (sourceName.Equals("Microsoft-Windows-WindowsUpdateClient", StringComparison.OrdinalIgnoreCase))
        {
            if (eventId is 19 or 23) return ReliabilityRecordCategory.Informational;
            if (eventId is 20 or 24) return ReliabilityRecordCategory.Failure;
        }

        if (sourceName.Equals("MsiInstaller", StringComparison.OrdinalIgnoreCase))
            return ReliabilityRecordCategory.Informational;

        if (sourceName.Equals("Application Hang", StringComparison.OrdinalIgnoreCase))
            return ReliabilityRecordCategory.Warning;

        if (sourceName.Equals("Application Error", StringComparison.OrdinalIgnoreCase)
            || sourceName.Contains("Windows Error Reporting", StringComparison.OrdinalIgnoreCase))
        {
            return ReliabilityRecordCategory.Failure;
        }

        string combined = $"{sourceName} {message}";
        bool mentionsFailure = combined.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("error", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("crash", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("stopped working", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("blue screen", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("bugcheck", StringComparison.OrdinalIgnoreCase);

        if (combined.Contains("hang", StringComparison.OrdinalIgnoreCase)) return ReliabilityRecordCategory.Warning;

        bool mentionsChange = combined.Contains("install", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("update", StringComparison.OrdinalIgnoreCase);

        if (mentionsChange && !mentionsFailure) return ReliabilityRecordCategory.Informational;
        if (mentionsFailure) return ReliabilityRecordCategory.Failure;

        return ReliabilityRecordCategory.Other;
    }

    // ==== #173: software-change log crossed with the unified timeline's crash clusters ====

    /// <summary>#173: clusters <paramref name="crashTimestamps"/> (crash-flagged entries from the
    /// unified incident timeline, #137) by grouping any two within 24 hours of each other into one
    /// cluster, then flags every Informational-category record in <paramref name="records"/> whose
    /// timestamp falls in the <paramref name="windowDays"/> before a cluster's earliest timestamp -
    /// mutates PrecedesCrashClusterNote on the matching records in place (the same "join in place
    /// after the fact" shape StabilityViewModel.RefreshTimelineExtrasAsync already uses to fold
    /// #143's attributions into #144's boot ledger's EndReason). A concrete "this appeared right
    /// before things got worse" list, explicitly correlation only - never claims cause.</summary>
    public static void CorrelateChangesWithCrashClusters(IEnumerable<ReliabilityRecordInfo> records, IReadOnlyList<DateTime> crashTimestamps, int windowDays = 5)
    {
        if (crashTimestamps.Count == 0) return;

        var sorted = crashTimestamps.OrderBy(t => t).ToList();
        var clusterOnsets = new List<DateTime> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            if ((sorted[i] - sorted[i - 1]) > TimeSpan.FromHours(24))
                clusterOnsets.Add(sorted[i]);
        }

        foreach (var record in records)
        {
            if (record.Category != ReliabilityRecordCategory.Informational) continue;

            var nearest = clusterOnsets
                .Where(onset => onset >= record.TimeGenerated && (onset - record.TimeGenerated).TotalDays <= windowDays)
                .OrderBy(onset => onset)
                .Cast<DateTime?>()
                .FirstOrDefault();

            if (nearest is { } onset)
            {
                double days = (onset - record.TimeGenerated).TotalDays;
                record.PrecedesCrashClusterNote =
                    $"Followed by a crash cluster starting {onset:g} ({days:0.#} day(s) later) - correlation only, not proof of cause.";
            }
        }
    }

    // ==== #171: on-demand RAC re-aggregation ====

    /// <summary>#171: `schtasks /run /tn "\Microsoft\Windows\RAC\RacTask"` - Reliability Monitor
    /// data is only aggregated when this task runs, so the last few hours of failures otherwise
    /// don't show up until it next fires on its own schedule. Same concurrent-read/bounded-timeout
    /// process-run shape as every other shelled-out tool in this app (see EtwTraceService's own
    /// RunCapturedAsync remarks) - translates the raw output into a plain-English result rather than
    /// a bare exit code, the same "quick flag, not a verdict" honesty EtwTraceService.ExplainWprError
    /// already applies to a different tool's errors. Not every Windows build ships this exact
    /// scheduled task (verified live while building this: it's absent on some recent builds even
    /// though the underlying WMI classes keep getting fresh data some other way) - that specific
    /// failure gets its own message rather than a raw "system cannot find the file" string.</summary>
    public static async Task<(bool Success, string Message)> RunRacTaskAsync(CancellationToken ct = default)
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("schtasks.exe", "/run /tn \"\\Microsoft\\Windows\\RAC\\RacTask\"", 30000, ct);

            if (output.Contains("cannot find the file specified", StringComparison.OrdinalIgnoreCase)
                || output.Contains("cannot find the task", StringComparison.OrdinalIgnoreCase)
                || output.Contains("ERROR:", StringComparison.OrdinalIgnoreCase) && output.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "The RAC scheduled task isn't present on this Windows build - Reliability Monitor data may still "
                    + "update on its own schedule; the data below reflects whatever was already aggregated.");
            }

            bool success = exitCode == 0 || output.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase);
            return success
                ? (true, "Reliability Monitor re-aggregation started - re-querying now (it may take a few seconds to finish on Windows' side).")
                : (false, string.IsNullOrWhiteSpace(output) ? "Couldn't run the RAC task (schtasks failed)." : output.Trim());
        }
        catch (Exception ex)
        {
            return (false, $"Couldn't run the RAC task: {ex.Message}");
        }
    }

    // ==== #172: WMIEnable read/write, with backup/revert (mirrors WerReportService's LocalDumps toggle) ====

    /// <summary>Reads HKLM\...\Reliability Analysis\WMI\WMIEnable - see ReliabilityAnalysisStatus's
    /// remarks for why the key/value being entirely absent means "enabled" (Windows' own default),
    /// not "disabled".</summary>
    public ReliabilityAnalysisStatus ReadAnalysisStatus()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ReliabilityAnalysisWmiPath);
            if (key is null) return new ReliabilityAnalysisStatus { KeyExists = false };

            return new ReliabilityAnalysisStatus
            {
                KeyExists = true,
                WmiEnableValue = key.GetValue("WMIEnable") as int?,
            };
        }
        catch
        {
            // Key unreadable (unexpected under an elevated process, but degrade rather than throw) -
            // treat the same as "not configured" (i.e. enabled, Windows' own default).
            return new ReliabilityAnalysisStatus { KeyExists = false };
        }
    }

    /// <summary>Writes WMIEnable=1 - the actual registry write for #172's "offer to re-enable it".
    /// StabilityViewModel is responsible for the explicit MessageBox confirmation (and for saving the
    /// pre-change value via SaveBackup below) before ever calling this - this method itself performs
    /// no confirmation of its own, same shape as WerReportService.WriteLocalDumpsSettings.</summary>
    public (bool Success, string? Error) EnableAnalysis()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(ReliabilityAnalysisWmiPath, writable: true);
            if (key is null) return (false, "Could not open or create the Reliability Analysis\\WMI registry key.");
            key.SetValue("WMIEnable", 1, RegistryValueKind.DWord);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Restores whatever WMIEnable looked like before #172's re-enable wrote to it - deletes
    /// the whole key if it didn't exist before (Windows' own default, collection enabled, then
    /// applies again with no key present), otherwise writes back the exact prior value. Mirrors
    /// WerReportService.RestoreLocalDumpsSettings.</summary>
    public (bool Success, string? Error) RestoreAnalysisStatus(ReliabilityAnalysisStatus previous)
    {
        try
        {
            if (!previous.KeyExists)
            {
                Registry.LocalMachine.DeleteSubKeyTree(ReliabilityAnalysisWmiPath, throwOnMissingSubKey: false);
                return (true, null);
            }

            using var key = Registry.LocalMachine.CreateSubKey(ReliabilityAnalysisWmiPath, writable: true);
            if (key is null) return (false, "Could not open the Reliability Analysis\\WMI registry key.");

            if (previous.WmiEnableValue is { } v) key.SetValue("WMIEnable", v, RegistryValueKind.DWord);
            else key.DeleteValue("WMIEnable", throwOnMissingValue: false);

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---- #172 backup persistence: same AppPaths.SettingsDirectory JSON-file shape
    // WerReportService uses for #165's LocalDumps revert. ----

    private static string BackupPath => AppPaths.GetPath("reliability-analysis-backup.json");

    public static bool BackupExists()
    {
        try { return File.Exists(BackupPath); }
        catch { return false; }
    }

    public static void SaveBackup(ReliabilityAnalysisStatus previous)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(previous, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort - worst case only the ViewModel's in-session "before" value survives */ }
    }

    public static ReliabilityAnalysisStatus? LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;
            return JsonSerializer.Deserialize<ReliabilityAnalysisStatus>(File.ReadAllText(BackupPath));
        }
        catch
        {
            return null; // corrupt/unreadable backup file - degrade to "no revert available"
        }
    }

    public static void ClearBackup()
    {
        try { if (File.Exists(BackupPath)) File.Delete(BackupPath); }
        catch { /* best-effort */ }
    }

    // ==== shared process runner (same shape as every other Services/* shell-out in this app) ====

    private static async Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs, CancellationToken ct)
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
            return ("(command timed out or was cancelled)", null);
        }

        string output = (await outputTask) + (await errorTask);
        return (output, proc.ExitCode);
    }
}
