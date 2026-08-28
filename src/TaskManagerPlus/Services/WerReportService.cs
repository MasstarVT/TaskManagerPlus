using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 16, items 38-49: Windows Error Reporting archive/queue scanning and management - a
/// crash record source entirely separate from the event log. Generalizes the same "a report
/// folder's Report.wer is a plain key=value text file, scan it for what you need" approach
/// EventLogService.ResolveWerReport (item 2) and MinidumpParserService.ResolveLiveKernelWerCode
/// (item 22) already use narrowly (one known Report Id / dump file name -> one matching folder)
/// into a full scan of every report currently on disk, machine-wide and per-user (item 40),
/// archive and queue (item 38).
/// Also #161-168 (a second, independently-built pass over the same WER archive/queue): an
/// instance-based reader (constructed with an EventLogExplorerService, hence non-static) that adds
/// bucket-signature clustering against a leaner WerReportInfo model, top-crashing-application
/// ranking joined against Application-log event 1000, "Application Hang" 1002 correlation, WER
/// storage footprint measurement, LocalDumps read/write/backup-and-revert, and the error-reporting
/// (Disabled/DontShowUI/consent/WerSvc) configuration check - kept alongside the items-38-49 static
/// API above rather than merged into it, since the two use different model shapes and callers
/// (EvidenceBundleService's export step, StabilityViewModel's WER-config/footprint/local-dumps
/// cards) already depend on this instance shape specifically.
/// </summary>
public sealed class WerReportService
{
    private readonly EventLogExplorerService _explorer;

    public WerReportService(EventLogExplorerService explorer) => _explorer = explorer;

    // ---------------------------------------------------------------------------------------
    // Items 38/40: scan roots.
    // ---------------------------------------------------------------------------------------

    private static List<(string Root, WerReportSource Source)> GetRoots()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new()
        {
            (Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"), WerReportSource.MachineArchive),
            (Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"), WerReportSource.MachineQueue),
            (Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive"), WerReportSource.UserArchive),
            (Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportQueue"), WerReportSource.UserQueue),
        };
    }

    /// <summary>Item 38: every report folder under all four roots, parsed. Best-effort per root
    /// and per folder - one inaccessible root or one malformed Report.wer doesn't stop the rest
    /// of the scan, the same "degrade to nothing found" tolerance every scan in this app uses.</summary>
    public static List<WerReport> ScanAll()
    {
        var result = new List<WerReport>();
        foreach (var (root, source) in GetRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    try
                    {
                        var report = ParseReportFolder(dir, source);
                        if (report is not null) result.Add(report);
                    }
                    catch { /* one malformed report folder shouldn't stop the rest of the scan */ }
                }
            }
            catch { /* root missing/access denied - contributes nothing */ }
        }
        return result.OrderByDescending(r => r.ReportTimestamp).ToList();
    }

    // ---------------------------------------------------------------------------------------
    // Item 38: generic Report.wer parse - direct keys first, Sig[N]/DynamicSig[N] pairs as a
    // fallback for whichever fields aren't present as a direct top-level key.
    // ---------------------------------------------------------------------------------------

    private static readonly Regex KeyValueLineRegex = new(@"^([^=\r\n\[\]]+)=(.*)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex SigNameRegex = new(@"^Sig\[(\d+)\]\.Name=(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SigValueRegex = new(@"^Sig\[(\d+)\]\.Value=(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DynamicSigNameRegex = new(@"^DynamicSig\[(\d+)\]\.Name=(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DynamicSigValueRegex = new(@"^DynamicSig\[(\d+)\]\.Value=(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static WerReport? ParseReportFolder(string dir, WerReportSource source)
    {
        var werFile = Path.Combine(dir, "Report.wer");
        if (!File.Exists(werFile)) return null;

        string text;
        try { text = File.ReadAllText(werFile); }
        catch { return null; }

        var direct = ParseDirectKeys(text);
        var sig = ParseSigPairs(text);

        string? Get(string directKey, string sigName)
        {
            if (direct.TryGetValue(directKey, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            return sig.TryGetValue(sigName, out var sv) && !string.IsNullOrWhiteSpace(sv) ? sv : null;
        }

        string? eventType = direct.GetValueOrDefault("EventType");
        string? appName = Get("AppName", "Application Name");
        string? appVersion = Get("AppVersion", "Application Version");
        string? appTimeStamp = Get("AppTimeStamp", "Application Timestamp");
        string? modName = Get("ModName", "Fault Module Name");
        string? modVersion = Get("ModVersion", "Fault Module Version");
        string? modTimeStamp = Get("ModTimeStamp", "Fault Module Timestamp");
        string? exceptionCode = Get("ExceptionCode", "Exception Code");
        string? offset = Get("Offset", "Exception Offset");

        string? bucketId = direct.GetValueOrDefault("BucketId")
            ?? direct.GetValueOrDefault("LegacyBucketId")
            ?? direct.GetValueOrDefault("Bucket")
            ?? direct.GetValueOrDefault("HashedBucket")
            ?? (sig.TryGetValue("Bucket ID", out var sb) ? sb : null);
        if (string.IsNullOrWhiteSpace(bucketId)) bucketId = null;

        bool isHang = !string.IsNullOrEmpty(eventType) && eventType.StartsWith("AppHang", StringComparison.OrdinalIgnoreCase);

        string computedSignature = string.Join("|", new[] { appName, appVersion, modName, modVersion, offset, exceptionCode }
            .Select(s => string.IsNullOrWhiteSpace(s) ? "?" : s!.Trim()))
            .ToUpperInvariant();

        var files = new List<string>();
        long totalSize = 0;
        try
        {
            foreach (var f in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (!string.IsNullOrEmpty(name)) files.Add(name);
                try { totalSize += new FileInfo(f).Length; } catch { /* one unreadable attached file doesn't stop the tally */ }
            }
        }
        catch { /* leave whatever was gathered, or empty */ }

        DateTime timestamp;
        try { timestamp = Directory.GetLastWriteTime(dir); }
        catch { timestamp = DateTime.MinValue; }

        return new WerReport
        {
            ReportFolder = dir,
            Source = source,
            EventType = eventType,
            IsHang = isHang,
            AppName = appName,
            AppVersion = appVersion,
            AppTimeStamp = appTimeStamp,
            ModName = modName,
            ModVersion = modVersion,
            ModTimeStamp = modTimeStamp,
            ExceptionCode = exceptionCode,
            Offset = offset,
            BucketId = bucketId,
            ComputedSignature = computedSignature,
            ReportTimestamp = timestamp,
            SizeBytes = totalSize,
            AttachedFiles = files,
        };
    }

    private static Dictionary<string, string> ParseDirectKeys(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in KeyValueLineRegex.Matches(text))
        {
            var key = m.Groups[1].Value.Trim();
            if (key.Length == 0) continue;
            if (!dict.ContainsKey(key)) dict[key] = m.Groups[2].Value.Trim();
        }
        return dict;
    }

    /// <summary>Combines the numbered Sig[N].Name/Sig[N].Value and DynamicSig[N].Name/
    /// DynamicSig[N].Value pairs into one Name-&gt;Value map keyed by the human-readable Name text
    /// (e.g. "Application Name") rather than the numeric index, which isn't stable across report
    /// shapes/Windows versions.</summary>
    private static Dictionary<string, string> ParseSigPairs(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddPairs(result, text, SigNameRegex, SigValueRegex, overwrite: true);
        AddPairs(result, text, DynamicSigNameRegex, DynamicSigValueRegex, overwrite: false);
        return result;
    }

    private static void AddPairs(Dictionary<string, string> into, string text, Regex nameRegex, Regex valueRegex, bool overwrite)
    {
        var names = new Dictionary<int, string>();
        var values = new Dictionary<int, string>();
        foreach (Match m in nameRegex.Matches(text))
            if (int.TryParse(m.Groups[1].Value, out var idx)) names[idx] = m.Groups[2].Value.Trim();
        foreach (Match m in valueRegex.Matches(text))
            if (int.TryParse(m.Groups[1].Value, out var idx)) values[idx] = m.Groups[2].Value.Trim();

        foreach (var (idx, name) in names)
        {
            if (string.IsNullOrEmpty(name) || !values.TryGetValue(idx, out var val)) continue;
            if (overwrite || !into.ContainsKey(name)) into[name] = val;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Item 39: bucket grouping.
    // ---------------------------------------------------------------------------------------

    public static List<WerBucketGroup> GroupByBucket(IEnumerable<WerReport> reports)
    {
        return reports
            .GroupBy(r => r.EffectiveBucketKey)
            .Select(g =>
            {
                var ordered = g.OrderByDescending(r => r.ReportTimestamp).ToList();
                return new WerBucketGroup
                {
                    BucketKey = g.Key,
                    HasRealBucketId = ordered[0].HasRealBucketId,
                    AppName = ordered.Select(r => r.AppName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "Unknown",
                    ModName = ordered.Select(r => r.ModName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "Unknown",
                    Count = ordered.Count,
                    LastSeen = ordered.Max(r => r.ReportTimestamp),
                    Reports = ordered,
                };
            })
            .OrderByDescending(b => b.Count)
            .ThenByDescending(b => b.LastSeen)
            .ToList();
    }

    // ---------------------------------------------------------------------------------------
    // Item 47: join to the Application-log event 1000 (Application Error) - ApplicationCrashEvent
    // (round 17, item 50) is read by EventLogService.ReadApplicationCrashEvents; this just does
    // the matching.
    // ---------------------------------------------------------------------------------------

    private const double AppErrorJoinWindowMinutes = 10;

    public static List<WerReport> JoinApplicationErrorEvents(List<WerReport> reports, List<ApplicationCrashEvent> events)
    {
        if (events.Count == 0) return reports;

        var result = new List<WerReport>(reports.Count);
        foreach (var r in reports)
        {
            if (r.IsHang || string.IsNullOrEmpty(r.AppName)) { result.Add(r); continue; }

            var match = events
                .Where(e => string.Equals(e.AppName, r.AppName, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(r.ModName) || string.IsNullOrEmpty(e.ModName) || string.Equals(e.ModName, r.ModName, StringComparison.OrdinalIgnoreCase))
                    && Math.Abs((e.TimeCreated - r.ReportTimestamp).TotalMinutes) <= AppErrorJoinWindowMinutes)
                .OrderBy(e => Math.Abs((e.TimeCreated - r.ReportTimestamp).TotalMinutes))
                .FirstOrDefault();

            result.Add(match is null ? r : r with { JoinedEventMessage = match.Message, JoinedEventReportId = match.ReportId });
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------
    // Round 17, item 53: join Application-log event 1002 ("Application Hang") to a matching WER
    // AppHang report - see ApplicationHangEvent's remarks on why HangType/HangSignature come from
    // here rather than a guessed positional read of event 1002 itself.
    // ---------------------------------------------------------------------------------------

    private const double AppHangJoinWindowMinutes = 10;

    public static List<ApplicationHangEvent> JoinApplicationHangEvents(List<ApplicationHangEvent> hangEvents, List<WerReport> hangReports)
    {
        if (hangEvents.Count == 0 || hangReports.Count == 0) return hangEvents;

        var result = new List<ApplicationHangEvent>(hangEvents.Count);
        foreach (var h in hangEvents)
        {
            // Prefer an exact Report Id match - WER's own report-folder name is frequently the
            // Report Id itself, so a substring check on the folder path catches it. Fall back to
            // a name+time-window match only when that's not available (an older report, or the
            // event's own Report Id couldn't be parsed).
            WerReport? match = null;
            if (!string.IsNullOrEmpty(h.ReportId))
                match = hangReports.FirstOrDefault(r => ReportFolderContainsReportId(r, h.ReportId!));

            match ??= hangReports
                .Where(r => !string.IsNullOrEmpty(h.ProcessName) && !string.IsNullOrEmpty(r.AppName)
                    && string.Equals(r.AppName, h.ProcessName, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs((r.ReportTimestamp - h.TimeCreated).TotalMinutes) <= AppHangJoinWindowMinutes)
                .OrderBy(r => Math.Abs((r.ReportTimestamp - h.TimeCreated).TotalMinutes))
                .FirstOrDefault();

            result.Add(match is null ? h : new ApplicationHangEvent
            {
                TimeCreated = h.TimeCreated,
                ProcessName = h.ProcessName,
                Version = h.Version,
                ProcessId = h.ProcessId,
                ApplicationPath = h.ApplicationPath,
                ReportId = h.ReportId,
                Message = h.Message,
                HangType = match.EventType,
                HangSignature = match.EffectiveBucketKey,
                // Item 69: preserve the sleep/resume flag EventLogService.ReadApplicationHangEvents
                // already computed on h - this constructor copy would otherwise silently reset it
                // to false for every hang that finds a WER match.
                HappenedDuringSleepResume = h.HappenedDuringSleepResume,
            });
        }
        return result;
    }

    /// <summary>Best-effort fallback for the Report Id match above - the report folder's own name
    /// on disk is frequently the Report Id itself (WER's own convention for a queue/archive
    /// folder), so a plain substring check on the folder path catches that shape too.</summary>
    private static bool ReportFolderContainsReportId(WerReport report, string reportId)
        => report.ReportFolder.IndexOf(reportId, StringComparison.OrdinalIgnoreCase) >= 0;

    // ---------------------------------------------------------------------------------------
    // Items 41/44: is WER even collecting data, and what does it do when it does.
    // ---------------------------------------------------------------------------------------

    private const string WerRootKeyPath = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting";
    private const string WerConsentKeyPath = WerRootKeyPath + @"\Consent";
    private const string WerServiceName = "WerSvc";

    public static WerCollectionStatus ReadCollectionStatus()
    {
        bool? disabled = null, dontSend = null, dontShowUi = null;
        int? defaultConsent = null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WerRootKeyPath);
            if (key is not null)
            {
                if (key.GetValue("Disabled") is { } d) disabled = Convert.ToInt32(d) != 0;
                if (key.GetValue("DontSendAdditionalData") is { } dsd) dontSend = Convert.ToInt32(dsd) != 0;
            }
        }
        catch { /* key/values not present, or access denied - "not set" */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WerConsentKeyPath);
            if (key is not null)
            {
                if (key.GetValue("DefaultConsent") is { } dc) defaultConsent = Convert.ToInt32(dc);
                if (key.GetValue("DontShowUI") is { } dsu) dontShowUi = Convert.ToInt32(dsu) != 0;
            }
        }
        catch { /* key/values not present, or access denied - "not set" */ }

        string serviceStatus = "Unknown";
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(WerServiceName);
            serviceStatus = sc.Status.ToString();
        }
        catch { serviceStatus = "Unavailable"; }

        bool serviceBlocked = false;
        try
        {
            // WerSvc is a demand-start service by design - "Stopped" is completely normal, not a
            // problem. Only a Start value of 4 (Disabled) means the service can never run at all.
            using var svcKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{WerServiceName}");
            if (svcKey?.GetValue("Start") is { } startVal && Convert.ToInt32(startVal) == 4)
                serviceBlocked = true;
        }
        catch { /* leave false - "not known to be blocked" */ }

        string consentText = defaultConsent switch
        {
            1 => "1 — always ask before sending",
            2 => "2 — automatically send parameters",
            3 => "3 — automatically send parameters and safe additional data",
            4 => "4 — automatically send all data (not recommended)",
            null => "Not set (Windows default applies)",
            _ => $"{defaultConsent} (unrecognized value)",
        };

        return new WerCollectionStatus
        {
            Disabled = disabled,
            DontSendAdditionalData = dontSend,
            ServiceStatusText = serviceStatus,
            ServiceLooksBlocked = serviceBlocked,
            DefaultConsent = defaultConsent,
            DefaultConsentText = consentText,
            DontShowUi = dontShowUi,
        };
    }

    /// <summary>Item 41's re-enable action - clears the two flags that stop WER from ever writing
    /// a report at all. Deliberately does not touch the Consent subkey's DefaultConsent/
    /// DontShowUI (item 44) - those control the crash dialog/telemetry-sending behavior, not
    /// whether a report is captured locally in the first place, and changing them isn't implied
    /// by "re-enable crash collection". Needs the elevated process this app already always runs
    /// as (CLAUDE.md's Elevation note), same as every other registry write in this app.</summary>
    public static bool EnableWer()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(WerRootKeyPath, writable: true);
            if (key is null) return false;
            key.SetValue("Disabled", 0, RegistryValueKind.DWord);
            key.SetValue("DontSendAdditionalData", 0, RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Item 43: queue/archive size + purge.
    // ---------------------------------------------------------------------------------------

    public static WerQueueSizeInfo ReadQueueSize()
    {
        int folders = 0;
        long size = 0;
        foreach (var (root, _) in GetRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    folders++;
                    try
                    {
                        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            try { size += new FileInfo(f).Length; } catch { /* one unreadable file doesn't stop the tally */ }
                        }
                    }
                    catch { /* one unreadable folder doesn't stop the tally */ }
                }
            }
            catch { /* root missing/access denied - contributes nothing to the tally */ }
        }
        return new WerQueueSizeInfo { FolderCount = folders, TotalSizeBytes = size };
    }

    /// <summary>Item 43: deletes every report folder under all four roots - destroys crash
    /// history (the UI carries an explicit warning + confirmation for this). Best-effort per
    /// folder; one locked/denied folder doesn't stop the rest.</summary>
    public static int PurgeAll()
    {
        int deleted = 0;
        foreach (var (root, _) in GetRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    try { Directory.Delete(dir, recursive: true); deleted++; }
                    catch { /* locked/denied - skip this one folder */ }
                }
            }
            catch { /* root missing/access denied - nothing to purge there */ }
        }
        return deleted;
    }

    // ---------------------------------------------------------------------------------------
    // Item 42: LocalDumps (per-app crash dump capture) read/write.
    // ---------------------------------------------------------------------------------------

    private const string LocalDumpsKeyPath = WerRootKeyPath + @"\LocalDumps";

    private static string LocalDumpsKeyPathFor(string? exeName)
        => string.IsNullOrWhiteSpace(exeName) ? LocalDumpsKeyPath : $@"{LocalDumpsKeyPath}\{exeName.Trim()}";

    public static LocalDumpsConfig ReadLocalDumpsConfig(string? exeName)
    {
        string path = LocalDumpsKeyPathFor(exeName);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key is null) return new LocalDumpsConfig { TargetExecutable = exeName, Exists = false };

            return new LocalDumpsConfig
            {
                TargetExecutable = exeName,
                Exists = true,
                DumpFolder = key.GetValue("DumpFolder") as string,
                DumpCount = key.GetValue("DumpCount") is { } dc ? Convert.ToInt32(dc) : null,
                DumpType = key.GetValue("DumpType") is { } dt ? Convert.ToInt32(dt) : null,
                CustomDumpFlags = key.GetValue("CustomDumpFlags") is { } cdf ? Convert.ToInt32(cdf) : null,
            };
        }
        catch
        {
            return new LocalDumpsConfig { TargetExecutable = exeName, Exists = false };
        }
    }

    /// <summary>Item 42: writes (creating the key if needed) DumpFolder/DumpCount/DumpType for
    /// either the global default (exeName null/blank) or a per-executable override subkey - a
    /// deliberate, explicit action, not automatic, matching this app's "expensive/impactful
    /// actions are explicit buttons" convention.</summary>
    public static bool WriteLocalDumpsConfig(string? exeName, string dumpFolder, int dumpCount, int dumpType)
    {
        string path = LocalDumpsKeyPathFor(exeName);
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
            if (key is null) return false;
            if (!string.IsNullOrWhiteSpace(dumpFolder))
                key.SetValue("DumpFolder", dumpFolder, RegistryValueKind.ExpandString);
            key.SetValue("DumpCount", dumpCount, RegistryValueKind.DWord);
            key.SetValue("DumpType", dumpType, RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes a LocalDumps override - deletes the per-exe subkey entirely, or just the
    /// four values this app itself writes when clearing the global default (leaving the parent
    /// LocalDumps key itself in place, since other tools/policies may also use it).</summary>
    public static bool ClearLocalDumpsConfig(string? exeName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exeName))
            {
                using var key = Registry.LocalMachine.OpenSubKey(LocalDumpsKeyPath, writable: true);
                if (key is null) return true;
                foreach (var name in new[] { "DumpFolder", "DumpCount", "DumpType", "CustomDumpFlags" })
                {
                    try { key.DeleteValue(name, throwOnMissingValue: false); } catch { /* best-effort */ }
                }
                return true;
            }

            Registry.LocalMachine.DeleteSubKeyTree(LocalDumpsKeyPathFor(exeName), throwOnMissingSubKey: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Item 48: long-horizon crash history from WER archive timestamps.
    // ---------------------------------------------------------------------------------------

    public static List<WerDailyCount> BuildLongHorizonHistory(List<WerReport> reports, int days)
    {
        var byDate = reports
            .Where(r => r.ReportTimestamp != DateTime.MinValue)
            .GroupBy(r => r.ReportTimestamp.Date)
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new List<WerDailyCount>();
        var today = DateTime.Now.Date;
        for (int i = days - 1; i >= 0; i--)
        {
            var day = today.AddDays(-i);
            result.Add(new WerDailyCount { Date = day, Count = byDate.TryGetValue(day, out var c) ? c : 0 });
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------
    // Item 45: copyable crash signature.
    // ---------------------------------------------------------------------------------------

    /// <summary>Item 45: the OS build string ("26100.2033") shared by both flavors of "Copy crash
    /// signature" below - read once per call from the same registry values Windows' own winver
    /// dialog derives its build number from; falls back to the plain CLR-reported OS version
    /// string when the key/values aren't present rather than fabricating a build number.</summary>
    public static string GetOsBuildString()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string? build = key?.GetValue("CurrentBuildNumber") as string;
            string? ubr = key?.GetValue("UBR")?.ToString();
            if (!string.IsNullOrEmpty(build))
                return string.IsNullOrEmpty(ubr) ? build : $"{build}.{ubr}";
        }
        catch { /* fall through to the plain CLR-reported version below */ }
        return Environment.OSVersion.Version.ToString();
    }

    /// <summary>Item 45: one line for the clipboard - bucket ID, app/module names and versions,
    /// exception code and OS build - for a WER report row.</summary>
    public static string BuildCrashSignatureText(WerReport r) =>
        $"Bucket: {r.EffectiveBucketKey} | App: {r.AppName ?? "Unknown"} {r.AppVersion ?? ""} | " +
        $"Module: {r.ModName ?? "Unknown"} {r.ModVersion ?? ""} | Exception: {r.ExceptionCode ?? "Unknown"} @ {r.Offset ?? "?"} | " +
        $"OS build: {GetOsBuildString()}";

    /// <summary>Item 45: the same one-line shape for a bugcheck/minidump row - stop code + raw
    /// parameters + OS build, instead of WER's app/module fields (a bugcheck has no faulting
    /// application/module in the WER sense).</summary>
    public static string BuildCrashSignatureText(string? bugcheckCode, IReadOnlyList<string> parameters) =>
        $"Bugcheck: {bugcheckCode ?? "Unknown"} ({string.Join(", ", parameters)}) | OS build: {GetOsBuildString()}";

    // =========================================================================================
    // #161-167: a second, instance-based pass over the same WER archive/queue (see the class
    // summary above for why this coexists with the static items-38-49 API rather than replacing
    // it) - parses Report.wer into WerReportInfo (#161), clusters into WerCrashBucket (#162),
    // ranks top crashing applications (#163), reads "Application Hang" 1002 entries (#164),
    // measures ReportQueue/ReportArchive disk footprint (#166), reads/writes the LocalDumps and
    // error-reporting-configuration registry keys (#165/#167), and #168's managed-exception detail
    // parse. Every read here degrades to empty/Unknown/null on failure rather than throwing or
    // fabricating a value, the same "degrade, never fabricate" rule the rest of this app's
    // Services/ layer follows. The one write path (#165's LocalDumps toggle) is never called from
    // here on its own - StabilityViewModel gates it behind an explicit MessageBox confirmation
    // first, per CLAUDE.md's "explicit permission required for a registry write" convention.
    // =========================================================================================

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
