using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 17, #859-870: Windows Defender / third-party AV / Attack Surface Reduction posture for the
/// Security tab's "Protection status" section. Reads root\Microsoft\Windows\Defender's
/// MSFT_MpComputerStatus/MSFT_MpPreference/MSFT_MpThreatDetection/MSFT_MpThreat WMI classes, the
/// Microsoft-Windows-Windows Defender/Operational event log (same EventLogReader/EventLogQuery shape
/// as EventLogService), and shells out to MpCmdRun.exe / "powershell -Command Start-MpWDOScan" - no
/// raw interop anywhere in this file, per CLAUDE.md's "prefer a known tool/API" rule.
///
/// Everything here is on-demand, wired to explicit buttons on the Security tab - nothing polls.
/// Cheap registry/WMI reads (status, freshness, exclusions, Tamper Protection, ASR configuration,
/// feature toggles, policy diagnosis, duplicated-scanner diagnosis) are grouped under one "Refresh
/// protection status" action; the genuinely slow bits (event-log timelines, the quarantine list, and
/// running an actual scan) each stay behind their own separate button, the same "expensive, so make
/// it explicit" split #845/#818 already use elsewhere in this app.
///
/// "Degrade to Unknown, never fabricate": every WMI property is read defensively (a whole class can
/// legitimately be unavailable - Defender uninstalled/replaced, or the query denied), and every field
/// on a successfully-returned instance is read independently, so one renamed/missing property on a
/// given Windows build degrades just that field to "Unknown" rather than failing the whole read. See
/// ReadTamperProtection's remarks for why several of these reads can come back empty specifically
/// BECAUSE Tamper Protection is on - that's surfaced as UI text, not just a code comment, per #866.
/// </summary>
public static class DefenderService
{
    private const string DefenderNamespace = @"root\Microsoft\Windows\Defender";
    private const string DefenderOperationalLog = "Microsoft-Windows-Windows Defender/Operational";
    private const int LookbackDays = 90;
    private const int MaxTimelineEvents = 50;

    // ==================================================================================
    // #859/#860/#861 (config half): MSFT_MpComputerStatus - protection state, signature/
    // platform freshness, and last-scan bookkeeping all live on this one WMI instance.
    // ==================================================================================

    public sealed class ComputerStatus
    {
        public bool Available { get; init; }
        public string? QueryError { get; init; }

        // #859
        public bool? RealTimeProtectionEnabled { get; init; }
        public bool? BehaviorMonitorEnabled { get; init; }
        public bool? OnAccessProtectionActive { get; init; }
        public bool? AntispywareEnabled { get; init; }
        public bool? AntivirusEnabled { get; init; }
        public string AmRunningMode { get; init; } = "Unknown";
        public bool? IsTamperProtected { get; init; }

        // Display-ready text for the four tri-state flags above, so SecurityView.xaml can bind
        // straight to a string instead of needing a bool?-aware converter - same "precompute a
        // *Text property" convention FeatureToggles/PolicyDiagnosis below already use.
        private static string TriStateText(bool? v) => v switch { true => "On", false => "Off", null => "Unknown" };
        public string RealTimeProtectionText => TriStateText(RealTimeProtectionEnabled);
        public string BehaviorMonitorText => TriStateText(BehaviorMonitorEnabled);
        public string OnAccessProtectionText => TriStateText(OnAccessProtectionActive);
        public string AntispywareEnabledText => TriStateText(AntispywareEnabled);
        public string AntivirusEnabledText => TriStateText(AntivirusEnabled);

        // #860
        public string AntivirusSignatureVersion { get; init; } = "Unknown";
        public string AntispywareSignatureVersion { get; init; } = "Unknown";
        public string AmEngineVersion { get; init; } = "Unknown";
        public string AmProductVersion { get; init; } = "Unknown";
        public DateTime? AntivirusSignatureLastUpdated { get; init; }
        public DateTime? AntispywareSignatureLastUpdated { get; init; }
        public int? AntivirusSignatureAgeDays => AntivirusSignatureLastUpdated is { } d ? Math.Max(0, (int)(DateTime.UtcNow - d).TotalDays) : null;
        public int? AntispywareSignatureAgeDays => AntispywareSignatureLastUpdated is { } d ? Math.Max(0, (int)(DateTime.UtcNow - d).TotalDays) : null;

        // #861 (the config half - QuickScanSource/timestamps; the event-log timeline is separate)
        public DateTime? QuickScanStartTime { get; init; }
        public DateTime? QuickScanEndTime { get; init; }
        public DateTime? FullScanStartTime { get; init; }
        public DateTime? FullScanEndTime { get; init; }
        public string LastQuickScanSource { get; init; } = "Unknown";
        public string QuickScanEndedText => QuickScanEndTime is { } d ? d.ToString("g") : "Never";
        public string FullScanEndedText => FullScanEndTime is { } d ? d.ToString("g") : "Never";
    }

    /// <summary>Stale-signature threshold (#860): "more than 3-4 days" per the item text.</summary>
    private const int StaleSignatureDays = 4;

    public static (ComputerStatus Status, List<SecurityFinding> Findings) ReadComputerStatus()
    {
        var findings = new List<SecurityFinding>();
        try
        {
            using var searcher = new ManagementObjectSearcher(DefenderNamespace, "SELECT * FROM MSFT_MpComputerStatus");
            foreach (ManagementObject mo in searcher.Get())
            {
                var status = new ComputerStatus
                {
                    Available = true,
                    RealTimeProtectionEnabled = TryGetBool(mo, "RealTimeProtectionEnabled"),
                    BehaviorMonitorEnabled = TryGetBool(mo, "BehaviorMonitorEnabled"),
                    // #859: exact property name varies by build - OnAccessProtectionEnabled is the
                    // commonly-documented one; NISEnabled (network inspection) is the closest
                    // related fallback signal when it's absent entirely.
                    OnAccessProtectionActive = TryGetBool(mo, "OnAccessProtectionEnabled") ?? TryGetBool(mo, "OnAccessProtectionActive") ?? TryGetBool(mo, "NISEnabled"),
                    AntispywareEnabled = TryGetBool(mo, "AntispywareEnabled"),
                    AntivirusEnabled = TryGetBool(mo, "AntivirusEnabled"),
                    AmRunningMode = TryGetString(mo, "AMRunningMode") ?? "Unknown",
                    IsTamperProtected = TryGetBool(mo, "IsTamperProtected"),

                    AntivirusSignatureVersion = TryGetString(mo, "AntivirusSignatureVersion") ?? "Unknown",
                    AntispywareSignatureVersion = TryGetString(mo, "AntispywareSignatureVersion") ?? "Unknown",
                    AmEngineVersion = TryGetString(mo, "AMEngineVersion") ?? "Unknown",
                    AmProductVersion = TryGetString(mo, "AMProductVersion") ?? "Unknown",
                    AntivirusSignatureLastUpdated = TryGetDateTime(mo, "AntivirusSignatureLastUpdated"),
                    AntispywareSignatureLastUpdated = TryGetDateTime(mo, "AntispywareSignatureLastUpdated"),

                    QuickScanStartTime = TryGetDateTime(mo, "QuickScanStartTime"),
                    QuickScanEndTime = TryGetDateTime(mo, "QuickScanEndTime"),
                    FullScanStartTime = TryGetDateTime(mo, "FullScanStartTime"),
                    FullScanEndTime = TryGetDateTime(mo, "FullScanEndTime"),
                    LastQuickScanSource = TryGetString(mo, "LastQuickScanSource") ?? "Unknown",
                };

                if (status.AntivirusSignatureAgeDays is { } avAge && avAge > StaleSignatureDays)
                {
                    findings.Add(new SecurityFinding
                    {
                        Severity = FindingSeverity.Medium,
                        Title = "Antivirus signatures are stale",
                        Reason = $"Antivirus definitions ({status.AntivirusSignatureVersion}) were last updated {avAge} day(s) ago - more than the {StaleSignatureDays}-day freshness bar.",
                        Path = "MSFT_MpComputerStatus.AntivirusSignatureLastUpdated",
                        WhatDisablingDoes = "Run Windows Update, or \"Check for updates\" under Windows Security > Virus & threat protection > Protection updates. Being offline for a while is the common innocent cause.",
                    });
                }
                if (status.AntispywareSignatureAgeDays is { } asAge && asAge > StaleSignatureDays)
                {
                    findings.Add(new SecurityFinding
                    {
                        Severity = FindingSeverity.Medium,
                        Title = "Antispyware signatures are stale",
                        Reason = $"Antispyware definitions ({status.AntispywareSignatureVersion}) were last updated {asAge} day(s) ago - more than the {StaleSignatureDays}-day freshness bar.",
                        Path = "MSFT_MpComputerStatus.AntispywareSignatureLastUpdated",
                        WhatDisablingDoes = "Run Windows Update, or \"Check for updates\" under Windows Security > Virus & threat protection > Protection updates.",
                    });
                }
                if (!status.AmRunningMode.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                    !status.AmRunningMode.Equals("Normal", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new SecurityFinding
                    {
                        Severity = FindingSeverity.Medium,
                        Title = $"Defender is running in {status.AmRunningMode}",
                        Reason = $"AMRunningMode reports \"{status.AmRunningMode}\", not the normal fully-active mode. Passive/EDR-block modes are expected when a third-party antivirus or an EDR product is managing protection instead - see the policy diagnosis below for whether that's the likely reason here.",
                        Path = "MSFT_MpComputerStatus.AMRunningMode",
                        WhatDisablingDoes = "If this wasn't intentional (no other antivirus/EDR is installed), check Windows Security for why Defender isn't fully active.",
                    });
                }

                return (status, findings);
            }
        }
        catch (Exception ex)
        {
            return (new ComputerStatus { Available = false, QueryError = ex.Message }, findings);
        }
        return (new ComputerStatus { Available = false, QueryError = "No MSFT_MpComputerStatus instance was returned - Defender's WMI provider may be unavailable (uninstalled/replaced by a third-party product, or access denied)." }, findings);
    }

    // ==================================================================================
    // #861/#862/#867: Defender Operational event-log timelines - one shared reader, three
    // different event-ID sets/labels. Same EventLogQuery/EventLogReader shape as
    // EventLogService.ReadLog - capped count, capped lookback, degrades to "nothing found"
    // on any failure (channel not enabled, access denied, ...) rather than throwing.
    // ==================================================================================

    public sealed record DefenderTimelineEvent(DateTime Time, int EventId, string EventType, string Summary);

    private static readonly Dictionary<int, string> ScanEventLabels = new()
    {
        [1000] = "Scan started",
        [1001] = "Scan completed",
        [1002] = "Scan completed (with warnings/stopped)",
    };

    private static readonly Dictionary<int, string> ThreatEventLabels = new()
    {
        [1116] = "Threat detected",
        [1117] = "Action taken on threat",
    };

    private static readonly Dictionary<int, string> AsrEventLabels = new()
    {
        [1121] = "ASR rule blocked",
        [1122] = "ASR rule audited",
    };

    /// <summary>#861: event timeline half - IDs 1000/1001/1002.</summary>
    public static List<DefenderTimelineEvent> ReadScanHistoryEvents(out bool logAvailable)
        => ReadOperationalEvents(new[] { 1000, 1001, 1002 }, ScanEventLabels, out logAvailable);

    /// <summary>#862: event timeline half - IDs 1116/1117.</summary>
    public static List<DefenderTimelineEvent> ReadThreatEvents(out bool logAvailable)
        => ReadOperationalEvents(new[] { 1116, 1117 }, ThreatEventLabels, out logAvailable);

    /// <summary>#867: event timeline half - IDs 1121/1122 ("what a rule actually stopped recently").</summary>
    public static List<DefenderTimelineEvent> ReadAsrEvents(out bool logAvailable)
        => ReadOperationalEvents(new[] { 1121, 1122 }, AsrEventLabels, out logAvailable);

    private static List<DefenderTimelineEvent> ReadOperationalEvents(int[] eventIds, IReadOnlyDictionary<int, string> labels, out bool logAvailable)
    {
        var result = new List<DefenderTimelineEvent>();
        logAvailable = true;
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            string idFilter = string.Join(" or ", eventIds.Select(id => $"EventID={id}"));
            var query = new EventLogQuery(DefenderOperationalLog, PathType.LogName,
                $"*[System[({idFilter}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxTimelineEvents && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    string label = labels.TryGetValue(record.Id, out var l) ? l : $"Event {record.Id}";
                    result.Add(new DefenderTimelineEvent(record.TimeCreated ?? DateTime.MinValue, record.Id, label, Truncate(message, 400)));
                }
            }
        }
        catch
        {
            // Operational log unavailable/access denied/channel not enabled - "nothing found",
            // callers show logAvailable=false as a distinct "couldn't read this log" message.
            logAvailable = false;
        }
        return result;
    }

    // ==================================================================================
    // #862 (WMI half): MSFT_MpThreatDetection (per-occurrence) joined against MSFT_MpThreat
    // (threat catalog, for the friendly name) by ThreatID - a flat combined list with the
    // event-log timeline above is an acceptable simpler shape per the item's own guidance
    // when a tighter join proves awkward; this still attempts the join since both classes
    // are simple flat WMI instances.
    // ==================================================================================

    public sealed record ThreatDetectionRecord(DateTime? DetectionTime, string ThreatName, string ProcessName, string Severity, bool? ActionSuccess);

    public static List<ThreatDetectionRecord> ReadThreatDetectionsWmi(out bool queryOk)
    {
        queryOk = false;
        var names = new Dictionary<string, (string Name, string Severity)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var threatSearcher = new ManagementObjectSearcher(DefenderNamespace, "SELECT ThreatID, ThreatName, SeverityID FROM MSFT_MpThreat");
            foreach (ManagementObject mo in threatSearcher.Get())
            {
                string? id = TryGetString(mo, "ThreatID");
                if (string.IsNullOrWhiteSpace(id)) continue;
                names[id] = (TryGetString(mo, "ThreatName") ?? "Unknown threat", SeverityText(TryGetInt(mo, "SeverityID")));
            }
        }
        catch
        {
            // MSFT_MpThreat unavailable - detections below still show with "Unknown threat" names.
        }

        var result = new List<ThreatDetectionRecord>();
        try
        {
            using var searcher = new ManagementObjectSearcher(DefenderNamespace, "SELECT ThreatID, ProcessName, InitialDetectionTime, ActionSuccess, SeverityID FROM MSFT_MpThreatDetection");
            foreach (ManagementObject mo in searcher.Get())
            {
                queryOk = true;
                string? threatId = TryGetString(mo, "ThreatID");
                var (name, severityFromCatalog) = threatId is not null && names.TryGetValue(threatId, out var n) ? n : ("Unknown threat", "Unknown");
                string severity = severityFromCatalog != "Unknown" ? severityFromCatalog : SeverityText(TryGetInt(mo, "SeverityID"));

                result.Add(new ThreatDetectionRecord(
                    TryGetDateTime(mo, "InitialDetectionTime"),
                    name,
                    TryGetString(mo, "ProcessName") ?? "Unknown",
                    severity,
                    TryGetBool(mo, "ActionSuccess")));
            }
        }
        catch
        {
            // No detections / class unavailable - an empty list, same "nothing found" degrade as
            // every other optional WMI class in this app.
        }

        return result.OrderByDescending(r => r.DetectionTime).ToList();
    }

    private static string SeverityText(int? severityId) => severityId switch
    {
        null => "Unknown",
        0 => "Unknown",
        1 => "Low",
        2 => "Moderate",
        4 => "High",
        5 => "Severe",
        _ => $"Unknown ({severityId})",
    };

    /// <summary>#862: High-severity findings for "action shown is Allowed" (a real detection that a
    /// user/exclusion let through) or a remediation that didn't succeed - built from whichever of
    /// the WMI/event-log sources actually returned something, per the item's guidance.</summary>
    public static List<SecurityFinding> BuildThreatFindings(List<ThreatDetectionRecord> wmiDetections, List<DefenderTimelineEvent> threatEvents)
    {
        var findings = new List<SecurityFinding>();

        foreach (var d in wmiDetections.Where(d => d.ActionSuccess == false))
        {
            findings.Add(new SecurityFinding
            {
                Severity = FindingSeverity.High,
                Title = $"Defender remediation failed: {d.ThreatName}",
                Reason = $"Defender detected \"{d.ThreatName}\" on {d.ProcessName}, but its remediation action did not succeed (ActionSuccess = false). Quick flag, not a verdict - re-run a scan or investigate manually.",
                Path = "MSFT_MpThreatDetection",
                WhatDisablingDoes = "Run a full scan, or manually investigate/remove the threat - a failed remediation means it may still be present.",
            });
        }

        foreach (var e in threatEvents.Where(e => e.EventId == 1117 && e.Summary.Contains("allow", StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(new SecurityFinding
            {
                Severity = FindingSeverity.High,
                Title = "A Defender detection was Allowed",
                Reason = $"Defender event 1117 on {e.Time:g} reports an \"Allow\" action taken on a detection - this typically means a user (or an exclusion) let a real detection run anyway. Quick flag, not a verdict - worth reviewing.",
                Path = "Microsoft-Windows-Windows Defender/Operational, event 1117",
                WhatDisablingDoes = "Review the full event in Event Viewer; if this wasn't intentional, re-scan and consider removing the corresponding exclusion.",
            });
        }

        return findings;
    }

    // ==================================================================================
    // #863: quarantine browser - MpCmdRun.exe -Restore -ListAll, parsed defensively line by
    // line (the exact column/label format isn't a documented, stable contract).
    // ==================================================================================

    public sealed record QuarantineItem(string ThreatName, string FilePath, string DetectionTimeText, string RawBlock);

    private static readonly Regex QuarantineDateRegex = new(@"\d{1,4}[-/]\d{1,2}[-/]\d{1,4}[ T]\d{1,2}:\d{2}(:\d{2})?", RegexOptions.Compiled);
    private static readonly Regex ThreatNameLineRegex = new(@"^(threat\s*name|threatname)\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PathLineRegex = new(@"^(path|resource)\s*[:=]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<QuarantineItem> ListQuarantine(out string? error)
    {
        error = null;
        string exe = ResolveMpCmdRunPath();
        var (exitCode, output) = RunCapturedWithExitCode(exe, "-Restore -ListAll", TimeSpan.FromSeconds(30));
        if (string.IsNullOrWhiteSpace(output))
        {
            error = exitCode == -1
                ? "MpCmdRun timed out listing quarantined items."
                : "MpCmdRun returned no output - either nothing is quarantined, or MpCmdRun.exe couldn't be found/run.";
            return new List<QuarantineItem>();
        }
        return ParseQuarantineList(output);
    }

    /// <summary>Defensive line-by-line parse: a new "ThreatName ="/"Threat name:" line starts a new
    /// item; every following line (until the next such line) is kept as raw context and scanned for
    /// a path-shaped line and a date-shaped substring. Never fabricates a path/date it can't find -
    /// falls back to an explicit "(not found in output)" placeholder instead.</summary>
    private static List<QuarantineItem> ParseQuarantineList(string output)
    {
        var items = new List<QuarantineItem>();
        string? currentThreat = null;
        string? currentFile = null;
        var blockLines = new List<string>();

        void Flush()
        {
            if (currentThreat is not null)
            {
                string block = string.Join(" | ", blockLines);
                var dateMatch = QuarantineDateRegex.Match(block);
                items.Add(new QuarantineItem(
                    currentThreat,
                    currentFile ?? "(path not found in output - see raw text)",
                    dateMatch.Success ? dateMatch.Value : "Unknown",
                    block));
            }
            currentThreat = null;
            currentFile = null;
            blockLines.Clear();
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var threatMatch = ThreatNameLineRegex.Match(line);
            if (threatMatch.Success)
            {
                Flush();
                currentThreat = threatMatch.Groups[2].Value.Trim();
                blockLines.Add(line);
                continue;
            }

            blockLines.Add(line);
            if (line.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                currentFile = line[5..].Trim();
            }
            else
            {
                var pathMatch = PathLineRegex.Match(line);
                if (pathMatch.Success) currentFile = pathMatch.Groups[2].Value.Trim();
            }
        }
        Flush();

        return items;
    }

    /// <summary>#863: restore syntax per the item's own text - MpCmdRun has no cleanly-documented
    /// per-item permanent-purge switch, so PurgeQuarantineItem below implements "purge" as restore-
    /// then-delete, the reasonable interpretation the item explicitly allows.</summary>
    public static (bool Success, string Output) RestoreQuarantineItem(string threatName)
    {
        var (exitCode, output) = RunCapturedWithExitCode(ResolveMpCmdRunPath(), $"-Restore -Name \"{threatName}\"", TimeSpan.FromSeconds(30));
        return (exitCode == 0, string.IsNullOrWhiteSpace(output) ? $"(exit code {exitCode}, no output)" : output.Trim());
    }

    public static (bool Success, string Output) PurgeQuarantineItem(string threatName, string filePath)
    {
        var (restored, restoreOutput) = RestoreQuarantineItem(threatName);
        if (!restored) return (false, $"Restore step failed, so nothing was deleted: {restoreOutput}");

        try
        {
            if (!string.IsNullOrWhiteSpace(filePath) && filePath != "(path not found in output - see raw text)" && File.Exists(filePath))
            {
                File.Delete(filePath);
                return (true, $"Restored, then deleted \"{filePath}\".");
            }
            return (false, $"Restored ({restoreOutput.Trim()}), but couldn't find \"{filePath}\" afterward to delete it automatically - remove it by hand if you don't want it back.");
        }
        catch (Exception ex)
        {
            return (false, $"Restored, but deleting the file afterward failed: {ex.Message}");
        }
    }

    // ==================================================================================
    // #864: run a scan (Quick/Full/folder/offline) with streamed, line-by-line output and
    // Cancel support. This service only builds the command/starts the process; the ViewModel
    // owns the Process reference (for Cancel/Kill) and marshals lines to the UI thread.
    // ==================================================================================

    public enum DefenderScanType { Quick = 1, Full = 2, Custom = 3 }

    public static string ResolveMpCmdRunPath()
    {
        try
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidate = Path.Combine(pf, "Windows Defender", "MpCmdRun.exe");
            if (File.Exists(candidate)) return candidate;
        }
        catch { /* fall through to PATH lookup */ }
        return "MpCmdRun.exe";
    }

    public static string BuildScanArgs(DefenderScanType type, string? customPath = null) => type switch
    {
        DefenderScanType.Quick => "-Scan -ScanType 1",
        DefenderScanType.Full => "-Scan -ScanType 2",
        DefenderScanType.Custom => $"-Scan -ScanType 3 -File \"{customPath}\"",
        _ => "-Scan -ScanType 1",
    };

    /// <summary>#864: Defender Offline scan trigger - MpCmdRun has no clean offline-scan switch;
    /// Start-MpWDOScan (Defender's own documented PowerShell cmdlet) is the reliable trigger, per
    /// the item's own guidance. Restarts the PC - the ViewModel shows a prominent warning and
    /// requires explicit confirmation before calling this.</summary>
    public static (string Exe, string Args) BuildOfflineScanCommand() => ("powershell.exe", "-NoProfile -Command \"Start-MpWDOScan\"");

    /// <summary>Starts a process with stdout/stderr redirected and streamed line-by-line via
    /// onLine (fired on a background thread - callers marshal to the UI thread themselves, the
    /// same pattern this app already uses elsewhere for cross-thread ObservableCollection updates).
    /// Returns the live, already-started Process so the caller can Kill() it to cancel.
    /// #1035: pass the Exited handler here rather than attaching it to the returned Process - a
    /// fast-exiting process (bad args) can fire Exited before the caller's subscription lands,
    /// permanently latching the caller's "scan running" state. Subscribing before Start() (with
    /// EnableRaisingEvents already true) guarantees the handler fires even for an instant exit.</summary>
    public static Process StartStreamingProcess(string exe, string args, Action<string> onLine, EventHandler? onExited = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        if (onExited is not null) proc.Exited += onExited; // #1035: before Start - see remarks
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }

    /// <summary>Blocking capture-with-exit-code, for the short-lived MpCmdRun calls that don't need
    /// streaming (quarantine list/restore/purge, and the Processes tab's "Scan this process's image
    /// file" trigger). #1084: delegates to the shared <see cref="ToolRunner"/>, keeping this
    /// method's historical timed-out shape (-1 plus "(timed out)").</summary>
    public static (int ExitCode, string Output) RunCapturedWithExitCode(string exe, string args, TimeSpan timeout)
    {
        var (output, exitCode) = ToolRunner.RunCaptured(exe, args, timeout, timeoutOutput: "(timed out)");
        return (exitCode ?? -1, output);
    }

    // ==================================================================================
    // #865: exclusion audit, extended - Paths/Extensions/Processes/IpAddresses/TemporaryPaths
    // from BOTH the local registry and the Policies registry hive (distinguishing the two)
    // AND MSFT_MpPreference, combined/deduped, with a high-risk flag per entry.
    // ==================================================================================

    public sealed record ExclusionEntry(string Category, string Value, string Source, bool IsHighRisk, string? RiskReason);

    private static readonly string[] ExclusionCategories = { "Paths", "Extensions", "Processes", "IpAddresses", "TemporaryPaths" };
    private static readonly string[] ScriptHostProcessNames = { "powershell.exe", "cmd.exe", "wscript.exe", "mshta.exe", "cscript.exe" };

    public static List<ExclusionEntry> ReadExclusionsExtended()
    {
        var raw = new List<ExclusionEntry>();
        ReadExclusionRegistryHive(@"SOFTWARE\Microsoft\Windows Defender\Exclusions", "Local (registry)", raw);
        ReadExclusionRegistryHive(@"SOFTWARE\Policies\Microsoft\Windows Defender\Exclusions", "Policy (registry)", raw);
        ReadExclusionsFromPreference(raw);

        return raw
            .GroupBy(e => $"{e.Category}\0{e.Value.Trim().ToLowerInvariant()}")
            .Select(g =>
            {
                var items = g.ToList();
                var display = items[0];
                var sources = string.Join(" + ", items.Select(x => x.Source).Distinct());
                bool risky = items.Any(x => x.IsHighRisk);
                string? reason = items.FirstOrDefault(x => x.RiskReason is not null)?.RiskReason;
                return new ExclusionEntry(display.Category, display.Value, sources, risky, reason);
            })
            .OrderBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReadExclusionRegistryHive(string basePath, string sourceLabel, List<ExclusionEntry> into)
    {
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(basePath);
            if (baseKey is null) return;

            foreach (var category in ExclusionCategories)
            {
                try
                {
                    using var sub = baseKey.OpenSubKey(category);
                    if (sub is null) continue;
                    foreach (var valueName in sub.GetValueNames())
                    {
                        if (string.IsNullOrWhiteSpace(valueName)) continue;
                        var (risky, reason) = AssessExclusionRisk(category, valueName);
                        into.Add(new ExclusionEntry(category, valueName, sourceLabel, risky, reason));
                    }
                }
                catch { /* one category shouldn't stop the rest */ }
            }
        }
        catch
        {
            // Hive/path inaccessible - Tamper Protection or a policy can deny even this elevated
            // process. Contributes nothing from this source; other sources may still succeed.
        }
    }

    private static void ReadExclusionsFromPreference(List<ExclusionEntry> into)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(DefenderNamespace,
                "SELECT ExclusionPath, ExclusionExtension, ExclusionProcess, ExclusionIpAddress FROM MSFT_MpPreference");
            foreach (ManagementObject mo in searcher.Get())
            {
                AddArrayExclusions(into, "Paths", TryGetStringArray(mo, "ExclusionPath"), "WMI (MSFT_MpPreference)");
                AddArrayExclusions(into, "Extensions", TryGetStringArray(mo, "ExclusionExtension"), "WMI (MSFT_MpPreference)");
                AddArrayExclusions(into, "Processes", TryGetStringArray(mo, "ExclusionProcess"), "WMI (MSFT_MpPreference)");
                AddArrayExclusions(into, "IpAddresses", TryGetStringArray(mo, "ExclusionIpAddress"), "WMI (MSFT_MpPreference)");
                break; // one instance expected
            }
        }
        catch
        {
            // Namespace/class unavailable, or Tamper Protection denies it - contribute nothing.
        }
    }

    private static void AddArrayExclusions(List<ExclusionEntry> into, string category, string[]? values, string source)
    {
        if (values is null) return;
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            var (risky, reason) = AssessExclusionRisk(category, v);
            into.Add(new ExclusionEntry(category, v, source, risky, reason));
        }
    }

    private static (bool IsHighRisk, string? Reason) AssessExclusionRisk(string category, string value)
    {
        switch (category)
        {
            case "Paths":
            case "TemporaryPaths":
            {
                string expanded;
                try { expanded = Environment.ExpandEnvironmentVariables(value); } catch { expanded = value; }

                if (Regex.IsMatch(expanded.Trim(), @"^[A-Za-z]:\\?$"))
                    return (true, $"Excludes an entire drive ({expanded}) - nothing on it is scanned.");

                var sensitiveRoots = new (string? Path, string Label)[]
                {
                    (SafeGetFolderPath(Environment.SpecialFolder.ApplicationData), "%AppData%"),
                    (SafeGetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LocalAppData%"),
                    (SafeGetFolderPath(Environment.SpecialFolder.UserProfile), "%UserProfile%"),
                    (Environment.GetEnvironmentVariable("TEMP"), "%Temp%"),
                };
                foreach (var (root, label) in sensitiveRoots)
                {
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    if (expanded.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        return (true, $"Excludes {label} ({root}) - a common malware-drop location - entirely from scanning.");
                }
                return (false, null);
            }
            case "Extensions":
            {
                var ext = value.TrimStart('.').ToLowerInvariant();
                return ext is "exe" or "dll"
                    ? (true, $"Excludes every .{ext} file from scanning, system-wide.")
                    : (false, null);
            }
            case "Processes":
            {
                return ScriptHostProcessNames.Any(h => value.Contains(h, StringComparison.OrdinalIgnoreCase))
                    ? (true, $"Excludes \"{value}\", a script host commonly abused for living-off-the-land attacks, from real-time scanning.")
                    : (false, null);
            }
            default:
                return (false, null);
        }
    }

    private static string? SafeGetFolderPath(Environment.SpecialFolder folder)
    {
        try { return Environment.GetFolderPath(folder); } catch { return null; }
    }

    public static List<SecurityFinding> BuildExclusionFindings(List<ExclusionEntry> exclusions)
    {
        var findings = new List<SecurityFinding>();
        foreach (var e in exclusions.Where(x => x.IsHighRisk))
        {
            bool driveRoot = e.RiskReason?.Contains("entire drive", StringComparison.OrdinalIgnoreCase) ?? false;
            findings.Add(new SecurityFinding
            {
                Severity = driveRoot ? FindingSeverity.High : FindingSeverity.Medium,
                Title = $"High-risk Defender exclusion: {e.Category} \"{e.Value}\"",
                Reason = e.RiskReason ?? "This exclusion is broader than typical and worth reviewing.",
                Path = $"Defender exclusions ({e.Source})",
                WhatDisablingDoes = "Remove the exclusion under Windows Security > Virus & threat protection > Exclusions (or the corresponding registry/policy value) if you don't recognize why it's there - a broad exclusion is a common way malware hides from real-time scanning after establishing persistence.",
            });
        }
        return findings;
    }

    // ==================================================================================
    // #866: Tamper Protection status - registry DWORD (as the item specifies) plus, when
    // available, MSFT_MpComputerStatus.IsTamperProtected as a second, more modern signal.
    // Always carries an explicit disclaimer in the returned text, not just a code comment.
    // ==================================================================================

    public sealed record TamperProtectionStatus(string State, string SourceText, string DisclaimerText);

    public static TamperProtectionStatus ReadTamperProtection(bool? wmiTamperProtected)
    {
        int? regState = null;
        int? regSource = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender\Features");
            if (key?.GetValue("TamperProtection") is int tp) regState = tp;
            if (key?.GetValue("TamperProtectionSource") is int src) regSource = src;
        }
        catch { /* Tamper Protection itself (or a policy) can deny this read - leave both null */ }

        string sourceText = regSource switch
        {
            null => "Unknown",
            0 => "Signature-based",
            1 => "User",
            2 => "ATP (Microsoft Defender for Endpoint)",
            3 => "M365 policy",
            4 => "Group Policy",
            _ => $"Unknown ({regSource})",
        };

        string state = wmiTamperProtected is { } wmi
            ? (wmi ? "On" : "Off")
            : regState is { } r
                ? (r != 0 ? "On" : "Off")
                : "Unknown";

        string disclaimer = wmiTamperProtected is null
            ? "The registry flag this reads does not reliably reflect the modern Tamper Protection toggle on Windows 10 1903+/Windows 11 (it moved to a different mechanism), and MSFT_MpComputerStatus.IsTamperProtected couldn't be read either. If several other Defender reads on this page come back empty or denied, Tamper Protection being ON is a likely explanation even though this page can't confirm that directly."
            : "Read from MSFT_MpComputerStatus.IsTamperProtected, a more reliable modern signal than the registry flag alone. If several other Defender reads on this page still come back empty or denied, Tamper Protection (or another policy) may still be restricting access even when this shows Off.";

        return new TamperProtectionStatus(state, sourceText, disclaimer);
    }

    // ==================================================================================
    // #867 (config half): per-rule ASR status from MSFT_MpPreference's parallel
    // AttackSurfaceReductionRules_Ids/_Actions arrays, mapped to friendly names via a
    // hardcoded lookup of the well-known published ASR rule GUIDs. A configured rule this
    // app doesn't recognize still shows (raw GUID as its name) rather than being dropped -
    // "better to show the raw GUID than guess wrong," per the item's own guidance.
    // ==================================================================================

    public sealed record AsrRuleStatus(string Id, string FriendlyName, string ActionText, bool IsUnknownRule);

    private static readonly Dictionary<string, string> AsrRuleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["D4F940AB-401B-4EFC-AADC-AD5F3C50688A"] = "Block Office applications from creating child processes",
        ["9E6C4E1F-7D60-472F-BA1A-A39EF669E4B2"] = "Block credential stealing from the Windows local security authority subsystem (lsass.exe)",
        ["BE9BA2D9-53EA-4CDC-84E5-9B1EEEE46550"] = "Block executable content from email client and webmail",
        ["D3E037E1-3EB8-44C8-A917-57927947596D"] = "Block JavaScript or VBScript from launching downloaded executable content",
        ["5BEB7EFE-FD9A-4556-801D-275E5FFC04CC"] = "Block execution of potentially obfuscated scripts",
        ["3B576869-A4EC-4529-8536-B80A7769E899"] = "Block Office applications from creating executable content",
        ["75668C1F-73B5-4CF0-BB93-3ECF5CB7CC84"] = "Block Office applications from injecting code into other processes",
        ["D1E49AAC-8F56-4280-B9BA-993A6D77406C"] = "Block process creations originating from PSExec and WMI commands",
        ["B2B3F03D-6A65-4F7B-A9C7-1C7EF74A9BA4"] = "Block untrusted and unsigned processes that run from USB",
        ["92E97FA1-2EDF-4476-BDD6-9DD0B4DDDC7B"] = "Block Win32 API calls from Office macros",
        ["01443614-CD74-433A-B99E-2ECDC07BFC25"] = "Block executable files from running unless they meet a prevalence, age, or trusted-list criterion",
        ["C1DB55AB-C21A-4637-BB3F-A12568109D35"] = "Use advanced protection against ransomware",
        ["26190899-1602-49E8-8B27-EB1D0A1CE869"] = "Block Office communication application from creating child processes",
        ["7674BA52-37EB-4A4F-A9A1-F0F9A1619A2C"] = "Block Adobe Reader from creating child processes",
        ["E6DB77E5-3DF2-4CF1-B95A-636979351E5B"] = "Block persistence through WMI event subscription",
        ["56A863A9-875E-4185-98A7-B882C64B5CE5"] = "Block abuse of exploited vulnerable signed drivers",
        ["A8F5898E-1DC8-49A9-9878-85004B8A61E6"] = "Block Webshell creation for servers",
    };

    private static string AsrActionText(int action) => action switch
    {
        0 => "Not configured",
        1 => "Block",
        2 => "Audit",
        6 => "Warn",
        _ => $"Unknown ({action})",
    };

    /// <summary>Lists every well-known ASR rule (so an unconfigured one still shows "Not
    /// configured" rather than being invisible), plus any configured rule not in the known-name
    /// table (raw GUID as its name).</summary>
    public static List<AsrRuleStatus> ReadAsrRules(out bool queryOk)
    {
        queryOk = false;
        var configured = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(DefenderNamespace,
                "SELECT AttackSurfaceReductionRules_Ids, AttackSurfaceReductionRules_Actions FROM MSFT_MpPreference");
            foreach (ManagementObject mo in searcher.Get())
            {
                queryOk = true;
                var ids = TryGetStringArray(mo, "AttackSurfaceReductionRules_Ids");
                var actions = TryGetIntArray(mo, "AttackSurfaceReductionRules_Actions");
                if (ids is not null)
                {
                    for (int i = 0; i < ids.Length; i++)
                    {
                        var id = ids[i].Trim('{', '}');
                        if (id.Length == 0) continue;
                        configured[id] = actions is not null && i < actions.Length ? actions[i] : 1; // action array shorter than ids - assume Block, the documented default when configured at all
                    }
                }
                break;
            }
        }
        catch
        {
            // Namespace/class unavailable or denied - every rule below reads "Not configured",
            // which callers should treat as "couldn't determine" given queryOk=false.
        }

        var result = new List<AsrRuleStatus>();
        foreach (var (id, name) in AsrRuleNames)
            result.Add(new AsrRuleStatus(id, name, configured.TryGetValue(id, out var a) ? AsrActionText(a) : "Not configured", false));

        foreach (var (id, action) in configured)
            if (!AsrRuleNames.ContainsKey(id))
                result.Add(new AsrRuleStatus(id, id, AsrActionText(action), true));

        return result.OrderBy(r => r.FriendlyName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ==================================================================================
    // #868: extra Defender feature toggles from MSFT_MpPreference.
    // ==================================================================================

    public sealed class FeatureToggles
    {
        public bool Available { get; init; }
        public string ControlledFolderAccessText { get; init; } = "Unknown";
        public List<string> ControlledFolderAllowedApps { get; init; } = new();
        public string NetworkProtectionText { get; init; } = "Unknown";
        public string PuaProtectionText { get; init; } = "Unknown";
        public bool PuaProtectionOff { get; init; }
        public string MapsReportingText { get; init; } = "Unknown";
        public string SubmitSamplesConsentText { get; init; } = "Unknown";
        public string CloudBlockLevelText { get; init; } = "Unknown";
    }

    public static FeatureToggles ReadFeatureToggles()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(DefenderNamespace, "SELECT * FROM MSFT_MpPreference");
            foreach (ManagementObject mo in searcher.Get())
            {
                int? cfa = TryGetInt(mo, "EnableControlledFolderAccess");
                int? np = TryGetInt(mo, "EnableNetworkProtection");
                int? pua = TryGetInt(mo, "PUAProtection");
                int? maps = TryGetInt(mo, "MAPSReporting");
                int? consent = TryGetInt(mo, "SubmitSamplesConsent");
                int? cloudBlock = TryGetInt(mo, "CloudBlockLevel");
                var apps = TryGetStringArray(mo, "ControlledFolderAccessAllowedApplications") ?? Array.Empty<string>();

                return new FeatureToggles
                {
                    Available = true,
                    ControlledFolderAccessText = cfa switch
                    {
                        0 => "Disabled", 1 => "Enabled", 2 => "Audit mode", 3 => "Block disk modification only",
                        null => "Unknown", _ => $"Unknown ({cfa})",
                    },
                    ControlledFolderAllowedApps = apps.ToList(),
                    NetworkProtectionText = np switch
                    {
                        0 => "Disabled", 1 => "Enabled", 2 => "Audit mode",
                        null => "Unknown", _ => $"Unknown ({np})",
                    },
                    PuaProtectionText = pua switch
                    {
                        0 => "Disabled (the default)", 1 => "Enabled", 2 => "Audit mode",
                        null => "Unknown", _ => $"Unknown ({pua})",
                    },
                    PuaProtectionOff = pua is 0 or null,
                    MapsReportingText = maps switch
                    {
                        0 => "Disabled", 1 => "Basic", 2 => "Advanced",
                        null => "Unknown", _ => $"Unknown ({maps})",
                    },
                    SubmitSamplesConsentText = consent switch
                    {
                        0 => "Always ask", 1 => "Auto-send safe samples", 2 => "Never send", 3 => "Auto-send all samples",
                        null => "Unknown", _ => $"Unknown ({consent})",
                    },
                    CloudBlockLevelText = cloudBlock switch
                    {
                        0 => "Default", 2 => "Moderate", 4 => "High", 6 => "High plus", 8 => "Zero tolerance",
                        null => "Unknown", _ => $"Unknown ({cloudBlock})",
                    },
                };
            }
        }
        catch
        {
            // Namespace/class unavailable or denied - "Unknown" everywhere via the default instance.
        }
        return new FeatureToggles { Available = false };
    }

    /// <summary>#868: PUA Protection is explicitly called out as "the single most relevant setting
    /// for the bloatware/adware problem this tab addresses" - surfaced as a Low/Info finding (not
    /// alarmist, since Disabled is the actual Windows default) so it still shows up in the findings
    /// list/exported report, on top of the dedicated UI callout.</summary>
    public static SecurityFinding? BuildPuaProtectionFinding(FeatureToggles toggles)
    {
        if (!toggles.Available || !toggles.PuaProtectionOff) return null;
        return new SecurityFinding
        {
            Severity = FindingSeverity.Low,
            Title = "Potentially Unwanted Application (PUA) protection is off",
            Reason = "PUAProtection is off, which is Windows' own default - not a misconfiguration, but it's the single most relevant Defender setting for the bloatware/adware/bundleware problem this tab is about. Enabling it blocks known PUA at download/install time.",
            Path = "MSFT_MpPreference.PUAProtection",
            WhatDisablingDoes = "Turn on \"Potentially unwanted app blocking\" under Windows Security > App & browser control > Reputation-based protection, or set-MpPreference -PUAProtection Enabled.",
        };
    }

    // ==================================================================================
    // #869: Defender-disabled-by-policy detection - policy registry values + service start
    // types (via System.ServiceProcess.ServiceController.StartType, the same property
    // ServiceControlService.Sample already reads for the Services tab) + a cross-reference
    // against the SecurityCenter2 AV product list.
    // ==================================================================================

    public sealed class PolicyDiagnosis
    {
        public bool AntiSpywareDisabledByPolicy { get; init; }
        public bool RealtimeDisabledByPolicy { get; init; }
        public Dictionary<string, string> ServiceStartTypes { get; init; } = new();
        public string Verdict { get; init; } = "Unknown";
        public string Detail { get; init; } = string.Empty;
        public FindingSeverity Severity { get; init; } = FindingSeverity.Info;
    }

    private static readonly string[] DefenderServiceNames = { "WinDefend", "WdNisSvc", "Sense", "SecurityHealthService" };

    public static PolicyDiagnosis DiagnosePolicyState(IReadOnlyList<AntivirusInfo> avProducts)
    {
        bool antiSpywareDisabled = ReadPolicyDword(@"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware") == 1;
        bool realtimeDisabled = ReadPolicyDword(@"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring") == 1;

        var startTypes = new Dictionary<string, string>();
        foreach (var svc in DefenderServiceNames)
            startTypes[svc] = ReadServiceStartType(svc);

        bool policyDisabled = antiSpywareDisabled || realtimeDisabled;
        var enabledThirdParty = avProducts
            .Where(p => p.LooksEnabled &&
                        !p.Name.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase) &&
                        !p.Name.Contains("Microsoft Defender", StringComparison.OrdinalIgnoreCase))
            .ToList();
        bool winDefendDisabledOrMissing = !startTypes.TryGetValue("WinDefend", out var wd) ||
            wd.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ||
            wd.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase);

        string verdict;
        string detail;
        FindingSeverity severity;

        if (policyDisabled)
        {
            var which = string.Join(", ", new[]
            {
                antiSpywareDisabled ? "DisableAntiSpyware" : null,
                realtimeDisabled ? "DisableRealtimeMonitoring" : null,
            }.Where(s => s is not null));
            verdict = "Off by policy";
            detail = $"A Group Policy value explicitly disables Defender ({which} = 1 under HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows Defender...).";
            severity = FindingSeverity.Info;
        }
        else if (enabledThirdParty.Count > 0 && winDefendDisabledOrMissing)
        {
            verdict = "Off, third-party AV active (expected)";
            detail = $"No policy disables Defender, but a third-party antivirus product looks active ({string.Join(", ", enabledThirdParty.Select(p => p.Name))}) - Defender typically goes passive automatically in this situation, which is expected and fine.";
            severity = FindingSeverity.Info;
        }
        else if (winDefendDisabledOrMissing)
        {
            verdict = "Off, no clear reason";
            detail = $"The WinDefend service is {(startTypes.TryGetValue("WinDefend", out var w) ? w : "not found")}, no policy value explains it, and no active third-party antivirus was detected - worth investigating.";
            severity = FindingSeverity.Medium;
        }
        else
        {
            verdict = "No issue detected";
            detail = "No policy disables Defender, and its core service isn't disabled.";
            severity = FindingSeverity.Info;
        }

        return new PolicyDiagnosis
        {
            AntiSpywareDisabledByPolicy = antiSpywareDisabled,
            RealtimeDisabledByPolicy = realtimeDisabled,
            ServiceStartTypes = startTypes,
            Verdict = verdict,
            Detail = detail,
            Severity = severity,
        };
    }

    public static SecurityFinding BuildPolicyFinding(PolicyDiagnosis diagnosis) => new()
    {
        Severity = diagnosis.Severity,
        Title = $"Defender policy/service diagnosis: {diagnosis.Verdict}",
        Reason = diagnosis.Detail,
        Path = @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender + service start types",
        WhatDisablingDoes = diagnosis.Severity >= FindingSeverity.Medium
            ? "Check Windows Security > Virus & threat protection for why Defender is off; if you expect a third-party antivirus to be covering this instead, verify it's actually installed and up to date."
            : "No action needed - this reflects an expected configuration.",
    };

    private static int? ReadPolicyDword(string keyPath, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as int?;
        }
        catch { return null; }
    }

    private static string ReadServiceStartType(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.StartType.ToString();
        }
        catch { return "Unknown (not found or inaccessible)"; }
    }

    // ==================================================================================
    // #870: duplicated real-time scanner detection - extends MultipleActiveAvWarning into a
    // real diagnosis by joining SecurityCenter2's enabled-product list against the
    // minifilter/kernel-driver entries AutorunsService's own Persistence scan already
    // produces (#819/#820) - reused via its public Scan() entry point rather than
    // re-implementing the fltmc/registry walk.
    // ==================================================================================

    /// <summary>Thin pass-through to SystemSpecsService's existing SecurityCenter2 read (made
    /// internal for this reuse) - avoids a second WMI query/productState decode for the same data.</summary>
    public static List<AntivirusInfo> ReadAntivirusProducts(out bool multipleActive) => SystemSpecsService.ReadAntivirusProducts(out multipleActive);

    public static SecurityFinding? DiagnoseDuplicatedRealTimeScanners(IReadOnlyList<AntivirusInfo> avProducts, IEnumerable<AutorunEntry> persistenceEntries)
    {
        var enabledProducts = avProducts.Where(p => p.LooksEnabled).ToList();
        if (enabledProducts.Count < 2) return null;

        var vendorFilters = persistenceEntries
            .Where(e => e.Category is "Minifilter" or "Kernel Driver")
            .Where(e => !LooksMicrosoft(e.Publisher) && !LooksMicrosoft(e.Name))
            .Select(e => (e.Name, Publisher: string.IsNullOrWhiteSpace(e.Publisher) ? "Unknown publisher" : e.Publisher))
            .Distinct()
            .ToList();

        if (vendorFilters.Count < 2) return null;

        var productNames = string.Join(", ", enabledProducts.Select(p => p.Name));
        var filterNames = string.Join(", ", vendorFilters.Select(f => $"{f.Name} ({f.Publisher})"));

        return new SecurityFinding
        {
            Severity = FindingSeverity.High,
            Title = "Multiple active real-time scanners detected",
            Reason = $"SecurityCenter2 reports {enabledProducts.Count} antivirus products currently enabled ({productNames}), and the Persistence scan found {vendorFilters.Count} distinct non-Microsoft minifilter/kernel-driver entries in the file I/O stack ({filterNames}). Running two real-time scanners at once is a common source of file-lock conflicts, false positives, and slowdowns - one is very likely a leftover from an uninstall that didn't fully clean up. Quick flag, not a verdict.",
            Path = @"root\SecurityCenter2\AntiVirusProduct + fltmc filters/instances (Persistence scan)",
            WhatDisablingDoes = "Use each vendor's own official removal tool to fully uninstall the one you don't want - a normal Add/Remove Programs uninstall commonly leaves its minifilter driver behind. Search \"<vendor name> removal tool\" (e.g. Norton Remove and Reinstall, McAfee Consumer Product Removal tool, Avast/AVG Clear) for the official one.",
        };
    }

    private static bool LooksMicrosoft(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

    // ==================================================================================
    // Shared defensive WMI property readers - every property read is independent, so one
    // renamed/missing field on a given Windows build degrades just that field, per #859's
    // own guidance ("read each property with try/catch per-field").
    // ==================================================================================

    private static bool? TryGetBool(ManagementBaseObject mo, string property)
    {
        try
        {
            var v = mo[property];
            return v switch { null => null, bool b => b, _ => Convert.ToBoolean(v) };
        }
        catch { return null; }
    }

    private static string? TryGetString(ManagementBaseObject mo, string property)
    {
        try { return (mo[property] as string)?.Trim(); } catch { return null; }
    }

    private static int? TryGetInt(ManagementBaseObject mo, string property)
    {
        try
        {
            var v = mo[property];
            return v is null ? null : Convert.ToInt32(v);
        }
        catch { return null; }
    }

    private static DateTime? TryGetDateTime(ManagementBaseObject mo, string property)
    {
        try
        {
            var v = mo[property];
            return v switch
            {
                string s when s.Length > 0 => ManagementDateTimeConverter.ToDateTime(s),
                DateTime dt => dt,
                _ => null,
            };
        }
        catch { return null; }
    }

    private static string[]? TryGetStringArray(ManagementBaseObject mo, string property)
    {
        try { return mo[property] as string[]; } catch { return null; }
    }

    private static int[]? TryGetIntArray(ManagementBaseObject mo, string property)
    {
        try { return mo[property] as int[]; } catch { return null; }
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
