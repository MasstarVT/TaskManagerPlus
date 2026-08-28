using System.IO;
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
/// </summary>
public static class WerReportService
{
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
}
