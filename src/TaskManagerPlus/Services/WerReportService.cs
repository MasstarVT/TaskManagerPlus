using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #161-167: Windows Error Reporting - parses the ReportQueue/ReportArchive Report.wer files (#161),
/// clusters them into bucket-signature rows (#162), ranks top crashing applications by combining WER
/// reports with Application-log "Application Error" 1000 entries (#163, reusing
/// EventLogExplorerService.ReadPage rather than adding a third event-log reader), reads "Application
/// Hang" 1002 entries separately (#164), measures the ReportQueue/ReportArchive trees' disk footprint
/// (#166), and reads/writes the LocalDumps and error-reporting-configuration registry keys (#165/#167).
/// Every read here degrades to empty/Unknown/null on failure rather than throwing or fabricating a
/// value - a locked-down ProgramData folder, a malformed Report.wer, or a denied registry key are all
/// real, expected conditions, the same "degrade, never fabricate" rule the rest of this app's
/// Services/ layer follows. The one write path (#165's LocalDumps toggle) is never called from here
/// on its own - StabilityViewModel gates it behind an explicit MessageBox confirmation first, per
/// CLAUDE.md's "explicit permission required for a registry write" convention.
/// </summary>
public sealed class WerReportService
{
    private readonly EventLogExplorerService _explorer;

    public WerReportService(EventLogExplorerService explorer) => _explorer = explorer;

    private const int LookbackDays = 30;
    private const string WerRootPath = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting";
    private const string LocalDumpsPath = WerRootPath + @"\LocalDumps";

    // ==== #161: WER report queue/archive explorer ====

    /// <summary>Enumerates every report folder under ReportQueue and ReportArchive, parses each
    /// Report.wer leniently, and returns everything within <paramref name="lookbackDays"/>, newest
    /// first - the same 30-day window every other Stability tab card uses.</summary>
    public List<WerReportInfo> ReadReports(int lookbackDays = LookbackDays)
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string queueRoot = Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue");
        string archiveRoot = Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive");

        var results = new List<WerReportInfo>();
        results.AddRange(ReadReportsFromRoot(queueRoot, isArchived: false));
        results.AddRange(ReadReportsFromRoot(archiveRoot, isArchived: true));

        var cutoff = DateTime.Now.AddDays(-lookbackDays);
        return results.Where(r => r.Timestamp >= cutoff).OrderByDescending(r => r.Timestamp).ToList();
    }

    private static List<WerReportInfo> ReadReportsFromRoot(string root, bool isArchived)
    {
        var results = new List<WerReportInfo>();
        try
        {
            if (!Directory.Exists(root)) return results;
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var info = ParseReportFolder(folder, isArchived);
                    if (info is not null) results.Add(info);
                }
                catch { /* one malformed/locked report folder shouldn't drop the rest of the scan */ }
            }
        }
        catch
        {
            // ReportQueue/ReportArchive missing or access denied - degrade to none from this root.
        }
        return results;
    }

    private static WerReportInfo? ParseReportFolder(string folderPath, bool isArchived)
    {
        string reportFile = Path.Combine(folderPath, "Report.wer");
        if (!File.Exists(reportFile)) return null;

        Dictionary<string, string> raw;
        try { raw = ParseLenientIni(reportFile); }
        catch { return null; } // unreadable/locked - skip this one report, not the whole scan

        DateTime timestamp;
        try { timestamp = File.GetLastWriteTime(reportFile); }
        catch { timestamp = DateTime.MinValue; }

        // Sig[N].Name / Sig[N].Value pairs - the report's own signature parameter list.
        var sigPairs = new List<(string Name, string Value)>();
        foreach (var key in raw.Keys.Where(k => k.StartsWith("Sig[", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".Name", StringComparison.OrdinalIgnoreCase)))
        {
            int close = key.IndexOf(']');
            if (close < 4) continue;
            string idx = key[4..close];
            if (raw.TryGetValue($"Sig[{idx}].Value", out var value))
                sigPairs.Add((raw[key], value));
        }

        string? FromSig(string name) => sigPairs.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value.Length: > 0 } hit ? hit.Value : null;

        string? bucketParam = raw.Keys
            .Where(k => k.StartsWith("BucketParameter", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => raw[k])
            .FirstOrDefault();

        return new WerReportInfo
        {
            FolderPath = folderPath,
            IsArchived = isArchived,
            Timestamp = timestamp,
            EventType = GetFirst(raw, "EventType"),
            AppName = GetFirst(raw, "AppName") ?? FromSig("Application Name"),
            AppPath = GetFirst(raw, "AppPath"),
            AppVersion = GetFirst(raw, "AppVersion") ?? FromSig("Application Version"),
            ModName = GetFirst(raw, "ModName") ?? FromSig("Fault Module Name"),
            ModVersion = GetFirst(raw, "ModVersion") ?? FromSig("Fault Module Version"),
            ExceptionCode = GetFirst(raw, "Code", "ExceptionCode") ?? FromSig("Exception Code"),
            ExceptionOffset = GetFirst(raw, "Offset", "ExceptionOffset") ?? FromSig("Exception Offset"),
            BucketId = GetFirst(raw, "BucketId", "LegacyBucketId"),
            BucketParameter = bucketParam,
            SignatureParameters = sigPairs,
        };
    }

    /// <summary>Report.wer is `Key=Value` per line (some Windows versions add `[Basic]`-style section
    /// headers, which are simply skipped) - not strictly-compliant INI, so this is a deliberately
    /// lenient line scan rather than a real INI parser. File.ReadLines auto-detects the file's own
    /// BOM (Report.wer is commonly UTF-16LE), so no explicit encoding is passed.</summary>
    private static Dictionary<string, string> ParseLenientIni(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '[' || line.StartsWith(";", StringComparison.Ordinal)) continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (key.Length == 0) continue;

            result[key] = value; // a duplicated key (shouldn't happen) just keeps the last value seen
        }
        return result;
    }

    private static string? GetFirst(Dictionary<string, string> raw, params string[] keys)
    {
        foreach (var k in keys)
            if (raw.TryGetValue(k, out var v) && v.Length > 0) return v;
        return null;
    }

    // ==== #162: group crashes by WER bucket signature ====

    /// <summary>Clusters reports by WerReportInfo.SignatureKey (BucketId when present, otherwise an
    /// app+module+exception-code composite) so five crashes of the same shape read as one row with a
    /// count - more precise than the existing #66 FaultingModuleSummary, which only ever groups by
    /// faulting module name.</summary>
    public List<WerCrashBucket> GroupByBucket(IEnumerable<WerReportInfo> reports)
    {
        return reports
            .GroupBy(r => r.SignatureKey)
            .Select(g =>
            {
                var first = g.First();
                return new WerCrashBucket
                {
                    AppName = first.AppName ?? "Unknown app",
                    ModName = first.ModName ?? "Unknown module",
                    ExceptionCode = first.ExceptionCode,
                    BucketId = first.BucketId,
                    Count = g.Count(),
                    FirstSeen = g.Min(r => r.Timestamp),
                    LastSeen = g.Max(r => r.Timestamp),
                };
            })
            .OrderByDescending(b => b.Count)
            .ThenByDescending(b => b.LastSeen)
            .ToList();
    }

    // ==== #163: top crashing applications ====

    private static readonly Regex AppCrashRegex = new(
        @"Faulting application name:\s*(?<app>[^,\r\n]+).*?Faulting module name:\s*(?<mod>[^,\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Combines WER report counts with Application-log "Application Error" 1000 entries into
    /// one ranked list - reuses EventLogExplorerService.ReadPage (the same reader the Events tab and
    /// #138's crash-window drill-down already use) with a narrow provider+eventId XPath, the same
    /// "name the exact IDs, don't sweep the whole log" shape EventLogService.ScanForKnownBadIds
    /// already uses, rather than adding a third event-log reader to this app.</summary>
    public List<TopCrashingApplication> ComputeTopCrashingApplications(IEnumerable<WerReportInfo> reports, int lookbackDays = LookbackDays, int maxApps = 10)
    {
        var byApp = new Dictionary<string, (int Count, Dictionary<string, int> Modules, DateTime LastSeen)>(StringComparer.OrdinalIgnoreCase);

        void Add(string? appRaw, string? module, DateTime when)
        {
            string key = string.IsNullOrWhiteSpace(appRaw) ? "Unknown app" : appRaw.Trim();
            if (!byApp.TryGetValue(key, out var entry))
                entry = (0, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), DateTime.MinValue);

            entry.Count++;
            if (!string.IsNullOrWhiteSpace(module))
                entry.Modules[module] = entry.Modules.TryGetValue(module, out var c) ? c + 1 : 1;
            if (when > entry.LastSeen) entry.LastSeen = when;
            byApp[key] = entry;
        }

        foreach (var r in reports)
            Add(r.AppName, r.ModName, r.Timestamp);

        try
        {
            long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
            string xpath = $"*[System[Provider[@Name={EventLogExplorerService.QuoteXPathLiteral("Application Error")}] and EventID=1000 and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]";
            var result = _explorer.ReadPage("Application", xpath, null, pageSize: 500);
            if (result.ErrorText is null)
            {
                foreach (var row in result.Rows)
                {
                    var m = AppCrashRegex.Match(row.Message ?? string.Empty);
                    if (!m.Success) continue;
                    Add(m.Groups["app"].Value.Trim(), m.Groups["mod"].Value.Trim(), row.TimeCreated);
                }
            }
        }
        catch { /* Application log unavailable - the WER-derived counts above still stand on their own */ }

        return byApp
            .Select(kv => new TopCrashingApplication
            {
                AppName = kv.Key,
                CrashCount = kv.Value.Count,
                MostCommonModule = kv.Value.Modules.Count == 0 ? null : kv.Value.Modules.OrderByDescending(m => m.Value).First().Key,
                LastCrashTime = kv.Value.LastSeen,
            })
            .OrderByDescending(a => a.CrashCount)
            .ThenByDescending(a => a.LastCrashTime)
            .Take(maxApps)
            .ToList();
    }

    // ==== #164: hang detection ====

    private static readonly Regex HangProgramRegex = new(@"^The program\s+(?<name>\S+)\s+version", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HangPidRegex = new(@"Process ID:\s*(?<pid>[0-9a-fA-Fx]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HangTypeRegex = new(@"Hang type:\s*(?<type>.+?)(?:\r?\n|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Queries the Application log for provider "Application Hang" event 1002 - kept as its
    /// own list rather than merged into the WER crash cards above, since "went white and
    /// unresponsive" (a hang) and "disappeared" (a crash) have different causes. CPU/disk-pressure
    /// correlation at each hang's timestamp is intentionally not attempted - see
    /// WerHangInfo.CorrelationNote for why (this app has no historical performance log to look a past
    /// timestamp up against yet).</summary>
    public List<WerHangInfo> ReadHangs(int lookbackDays = LookbackDays, int maxRecords = 200)
    {
        var results = new List<WerHangInfo>();
        try
        {
            long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
            string xpath = $"*[System[Provider[@Name={EventLogExplorerService.QuoteXPathLiteral("Application Hang")}] and EventID=1002 and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]";
            var result = _explorer.ReadPage("Application", xpath, null, pageSize: maxRecords);
            if (result.ErrorText is not null) return results;

            foreach (var row in result.Rows)
            {
                string message = row.Message ?? string.Empty;
                results.Add(new WerHangInfo
                {
                    Timestamp = row.TimeCreated,
                    ProcessName = HangProgramRegex.Match(message) is { Success: true } pm ? pm.Groups["name"].Value.Trim() : null,
                    ProcessId = HangPidRegex.Match(message) is { Success: true } id ? id.Groups["pid"].Value.Trim() : null,
                    HangType = HangTypeRegex.Match(message) is { Success: true } ht ? ht.Groups["type"].Value.Trim() : null,
                    RawMessage = message,
                });
            }
        }
        catch { /* Application log unavailable - degrade to no hangs found */ }

        return results.OrderByDescending(h => h.Timestamp).ToList();
    }

    // ==== #166: WER storage footprint ====

    /// <summary>Total size and file count of the ReportQueue/ReportArchive trees - a reveal-in-
    /// Explorer button is offered next to each (reusing EtwTraceService.RevealInExplorer, the same
    /// helper #159's stale-artifact list already uses, rather than a second copy of the
    /// `explorer.exe /select,` logic); this app never deletes anything from either folder.</summary>
    public WerStorageFootprint ComputeStorageFootprint()
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string queueRoot = Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue");
        string archiveRoot = Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive");

        var (queueExists, queueSize, queueCount) = MeasureTree(queueRoot);
        var (archiveExists, archiveSize, archiveCount) = MeasureTree(archiveRoot);

        return new WerStorageFootprint
        {
            QueuePath = queueRoot,
            QueueExists = queueExists,
            QueueSizeBytes = queueSize,
            QueueFileCount = queueCount,
            ArchivePath = archiveRoot,
            ArchiveExists = archiveExists,
            ArchiveSizeBytes = archiveSize,
            ArchiveFileCount = archiveCount,
        };
    }

    private static (bool Exists, long SizeBytes, int FileCount) MeasureTree(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return (false, 0, 0);

            long size = 0;
            int count = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    size += new FileInfo(file).Length;
                    count++;
                }
                catch { /* one unreadable file shouldn't drop the rest of the tally */ }
            }
            return (true, size, count);
        }
        catch
        {
            // Folder missing or access denied mid-walk - degrade to "nothing measurable".
            return (false, 0, 0);
        }
    }

    // ==== #165: local crash dump capture (LocalDumps) - read, write (with caller-gated confirmation), and backup/revert ====

    /// <summary>Reads the current LocalDumps configuration - KeyExists=false means the subkey doesn't
    /// exist at all (Windows' own default upload-and-discard behavior applies), not "explicitly
    /// disabled".</summary>
    public LocalDumpsSettings ReadLocalDumpsSettings()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(LocalDumpsPath);
            if (key is null) return new LocalDumpsSettings { KeyExists = false };

            return new LocalDumpsSettings
            {
                KeyExists = true,
                DumpFolder = key.GetValue("DumpFolder") as string,
                DumpCount = key.GetValue("DumpCount") as int?,
                DumpType = key.GetValue("DumpType") as int?,
            };
        }
        catch
        {
            // Key unreadable (unexpected under an elevated process, but degrade rather than throw) -
            // treat the same as "not configured".
            return new LocalDumpsSettings { KeyExists = false };
        }
    }

    /// <summary>Writes DumpFolder/DumpCount/DumpType under LocalDumps - the actual registry write for
    /// #165's toggle. StabilityViewModel is responsible for the explicit MessageBox confirmation (and
    /// for saving the pre-change values via SaveBackup below) before ever calling this; this method
    /// itself performs no confirmation of its own, matching StartupManagerService.SetEnabled's
    /// "the write itself is unconditional, the caller decides whether to call it" shape.</summary>
    public (bool Success, string? Error) WriteLocalDumpsSettings(string dumpFolder, int dumpCount, int dumpType)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(LocalDumpsPath, writable: true);
            if (key is null) return (false, "Could not open or create the LocalDumps registry key.");

            // Best-effort - WER creates the folder itself on the first dump it writes, but creating
            // it now means the "reveal in Explorer" affordance works immediately instead of erroring
            // until a crash actually happens.
            try { Directory.CreateDirectory(dumpFolder); } catch { /* not fatal - WER will still create it on first use */ }

            key.SetValue("DumpFolder", dumpFolder, RegistryValueKind.ExpandString);
            key.SetValue("DumpCount", dumpCount, RegistryValueKind.DWord);
            key.SetValue("DumpType", dumpType, RegistryValueKind.DWord);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Restores whatever LocalDumps looked like before #165's toggle wrote to it - deletes
    /// the whole subkey if it didn't exist before, otherwise writes back exactly the prior
    /// DumpFolder/DumpCount/DumpType (including removing a value that didn't exist before, rather
    /// than leaving it set).</summary>
    public (bool Success, string? Error) RestoreLocalDumpsSettings(LocalDumpsSettings previous)
    {
        try
        {
            if (!previous.KeyExists)
            {
                Registry.LocalMachine.DeleteSubKeyTree(LocalDumpsPath, throwOnMissingSubKey: false);
                return (true, null);
            }

            using var key = Registry.LocalMachine.CreateSubKey(LocalDumpsPath, writable: true);
            if (key is null) return (false, "Could not open the LocalDumps registry key.");

            if (previous.DumpFolder is not null) key.SetValue("DumpFolder", previous.DumpFolder, RegistryValueKind.ExpandString);
            else key.DeleteValue("DumpFolder", throwOnMissingValue: false);

            if (previous.DumpCount is { } c) key.SetValue("DumpCount", c, RegistryValueKind.DWord);
            else key.DeleteValue("DumpCount", throwOnMissingValue: false);

            if (previous.DumpType is { } t) key.SetValue("DumpType", t, RegistryValueKind.DWord);
            else key.DeleteValue("DumpType", throwOnMissingValue: false);

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---- #165 backup persistence: the pre-change values, so "revert" survives an app restart too,
    // not just the current session - same AppPaths.SettingsDirectory JSON-file shape every other
    // persisted setting in this app uses (see PollIntervalSettingsService for the reference shape). ----

    private static string BackupPath => AppPaths.GetPath("wer-localdumps-backup.json");

    public static bool BackupExists()
    {
        try { return File.Exists(BackupPath); }
        catch { return false; }
    }

    public static void SaveBackup(LocalDumpsSettings previous)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(previous, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort - worst case only the ViewModel's in-session "before" values survive */ }
    }

    public static LocalDumpsSettings? LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;
            return JsonSerializer.Deserialize<LocalDumpsSettings>(File.ReadAllText(BackupPath));
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

    // ==== #167: error reporting configuration check ====

    /// <summary>Reads whether WER is turned off machine-wide - HKLM's Disabled/DontShowUI/consent
    /// keys plus the WerSvc service state (via the same System.ServiceProcess.ServiceController API
    /// this app already uses elsewhere). Purely informational (CLAUDE.md's "quick flag, not a
    /// verdict") - WerSvc is a manual/trigger-start service, so it merely sitting Stopped is normal
    /// and is deliberately not treated as "reporting is off" here; only the registry Disabled flag or
    /// the service's start type actually being set to Disabled counts.</summary>
    public WerConfigStatus ReadConfigStatus()
    {
        bool? disabled = null;
        bool? dontShowUi = null;
        string? consentDescription = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WerRootPath);
            if (key is not null)
            {
                if (key.GetValue("Disabled") is int d) disabled = d != 0;
                if (key.GetValue("DontShowUI") is int u) dontShowUi = u != 0;
                try
                {
                    using var consentKey = key.OpenSubKey("Consent");
                    if (consentKey?.GetValue("DefaultConsent") is int dc) consentDescription = DescribeConsent(dc);
                }
                catch { /* Consent subkey unreadable - leave the description null rather than guess */ }
            }
        }
        catch { /* WER root key unreadable/absent - Disabled/DontShowUI/consent all stay Unknown (null) */ }

        string werSvcStatus = "Unknown";
        string? werSvcStartType = null;
        try
        {
            using var sc = new ServiceController("WerSvc");
            werSvcStatus = sc.Status.ToString();
            try { werSvcStartType = sc.StartType.ToString(); }
            catch { /* StartType read failed independently of Status - leave it null */ }
        }
        catch { /* WerSvc missing/inaccessible on this Windows edition */ }

        bool isOff = disabled == true || string.Equals(werSvcStartType, "Disabled", StringComparison.OrdinalIgnoreCase);

        return new WerConfigStatus
        {
            Disabled = disabled,
            DontShowUI = dontShowUi,
            ConsentDescription = consentDescription,
            WerSvcStatus = werSvcStatus,
            WerSvcStartType = werSvcStartType,
            IsReportingEffectivelyOff = isOff,
        };
    }

    /// <summary>Best-effort label for the Consent\DefaultConsent DWORD - the same "documented-enough
    /// convention, not a guess" tier CLAUDE.md's AV/mitigation-status reads already sit at (an
    /// undocumented-but-known bitmask/registry convention, informational only). Falls back to the raw
    /// numeric value for anything outside Microsoft's four documented consent levels rather than
    /// inventing a label for it.</summary>
    private static string DescribeConsent(int value) => value switch
    {
        1 => "Ask me every time (1)",
        2 => "Never send data (2)",
        3 => "Send parameters automatically (3)",
        4 => "Send parameters and safe additional data automatically (4)",
        _ => $"Unrecognized consent level ({value})",
    };

    // ==== #168: managed exception detail for .NET Runtime 1026 / Application Error 1000 ====

    private static readonly Regex ExceptionInfoRegex = new(
        @"Exception Info:\s*(?<type>\S+):?\s*(?<msg>.*?)(?:\r?\n(?!\s+at\s)|\z)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex StackFrameRegex = new(@"^\s*at\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex AppCrash1000DetailRegex = new(
        @"Faulting application name:\s*(?<appName>[^,\r\n]+),\s*version:\s*(?<appVer>[^,\r\n]+).*?" +
        @"Faulting module name:\s*(?<modName>[^,\r\n]+),\s*version:\s*(?<modVer>[^,\r\n]+).*?" +
        @"Exception code:\s*(?<code>0x[0-9a-fA-F]+).*?" +
        @"Fault offset:\s*(?<offset>0x[0-9a-fA-F]+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>#168: for ".NET Runtime" event 1026, parses "Exception Info: Type: message" plus up
    /// to 5 "at ..." stack frames straight out of the event's own formatted message - the actual
    /// managed exception type and top frames, not just a truncated blob. For "Application Error"
    /// event 1000, there is no managed stack trace to parse (that only ever appears in 1026's
    /// message; 1000 is the generic native-crash report) - honestly, this instead surfaces 1000's own
    /// structured faulting-application/module/exception-code/offset fields in full rather than
    /// pretending a stack trace exists where none does. Returns null (caller falls back to the
    /// existing truncated Message) for any other event ID, or when the message doesn't match the
    /// expected shape at all.</summary>
    public static string? ParseManagedExceptionDetail(int eventId, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        try
        {
            if (eventId == 1026)
            {
                var m = ExceptionInfoRegex.Match(message);
                if (!m.Success) return null;

                var frames = StackFrameRegex.Matches(message).Take(5).Select(f => "  at " + f.Groups[1].Value.Trim()).ToList();

                var sb = new StringBuilder();
                sb.Append("Exception: ").Append(m.Groups["type"].Value.Trim());
                if (m.Groups["msg"].Value.Trim() is { Length: > 0 } msg) sb.Append(" - ").Append(msg);
                if (frames.Count > 0)
                {
                    sb.AppendLine().AppendLine("Top stack frames:");
                    foreach (var f in frames) sb.AppendLine(f);
                }
                return sb.ToString().TrimEnd();
            }

            if (eventId == 1000)
            {
                var m = AppCrash1000DetailRegex.Match(message);
                if (!m.Success) return null;

                return $"Faulting application: {m.Groups["appName"].Value.Trim()} (v{m.Groups["appVer"].Value.Trim()})\n" +
                       $"Faulting module: {m.Groups["modName"].Value.Trim()} (v{m.Groups["modVer"].Value.Trim()})\n" +
                       $"Exception code: {m.Groups["code"].Value.Trim()}, offset {m.Groups["offset"].Value.Trim()}\n" +
                       "(A native crash report - only .NET Runtime event 1026, not this one, carries a managed stack trace.)";
            }
        }
        catch
        {
            // Malformed/unexpected message shape for this event ID - degrade to null so the caller
            // falls back to the existing truncated Message, never a partial/garbled parse.
        }
        return null;
    }
}
