using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Lists and controls Windows Scheduled Tasks (#79/#80) - a huge, often-overlooked source of
/// background slowdowns and unwanted auto-launches that the Startup tab's registry-Run/Startup-
/// folder scan doesn't cover at all. Shells out to schtasks.exe rather than the Task Scheduler
/// COM API (ITaskService) - this app takes no COM interop dependency anywhere else, and
/// schtasks' CSV/XML output is a stable, documented contract, the same "known Windows tool, not
/// raw interop" tradeoff ServiceControlService.ReadFailureActionsText and
/// NetworkDiagnosticsService's `netsh wlan` parsing already take.
/// </summary>
public static class ScheduledTaskService
{
    /// <summary>Enumerates every registered task. Can take a couple of seconds on a system with
    /// hundreds of tasks - callers should treat this as an explicit, on-demand action (a "Load
    /// scheduled tasks" button), not something to run on a tick.</summary>
    public static async Task<List<ScheduledTaskRow>> ListAsync()
    {
        var rows = await QueryAsync();
        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// #292: extends this Startup-tab inventory with a live "what's running right now" view -
    /// the same `/query /fo csv /v` call, just filtered to Status=Running - so the Responsiveness
    /// tab's background-activity ribbon/card can answer "what task fired at the exact second things
    /// hitched" (feeding a future flight-recorder timeline, items #296-300, not built here). A
    /// fresh shell-out rather than reusing a cached ListAsync result, since the two call sites
    /// (Startup tab's full inventory vs. this tab's live-running view) refresh independently.
    /// </summary>
    public static async Task<List<ScheduledTaskRow>> ListRunningAsync()
    {
        var rows = await QueryAsync();
        return rows
            .Where(r => r.Status.Equals("Running", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<List<ScheduledTaskRow>> QueryAsync()
    {
        var rows = new List<ScheduledTaskRow>();
        try
        {
            string output = (await RunCapturedAsync("schtasks.exe", "/query /fo csv /v")).Output;
            var lines = SplitCsvLines(output);
            if (lines.Count < 2) return rows;

            var header = ParseCsvLine(lines[0]);
            int Idx(string name) => header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
            int iName = Idx("TaskName"), iStatus = Idx("Status"), iNext = Idx("Next Run Time"),
                iLast = Idx("Last Run Time"), iResult = Idx("Last Result"), iAuthor = Idx("Author"),
                iRun = Idx("Task To Run");
            if (iName < 0) return rows;

            // A task with multiple triggers gets one CSV row per trigger in /v mode - dedupe by
            // name, keeping the first (they share the same Status/NextRunTime/etc. fields anyway).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Count; i++)
            {
                var fields = ParseCsvLine(lines[i]);
                if (fields.Count <= iName) continue;
                string name = fields[iName];
                if (name.Length == 0 || name.Equals("TaskName", StringComparison.OrdinalIgnoreCase) || !seen.Add(name))
                    continue;

                string status = At(fields, iStatus);
                rows.Add(new ScheduledTaskRow
                {
                    Name = name,
                    Status = status,
                    NextRunTime = At(fields, iNext),
                    LastRunTime = At(fields, iLast),
                    LastResult = At(fields, iResult),
                    Author = At(fields, iAuthor),
                    TaskToRun = At(fields, iRun),
                    IsEnabled = !status.Equals("Disabled", StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        catch
        {
            // schtasks unavailable/failed - empty list, same as every other optional data source
            // in this app degrades on failure.
        }
        return rows;
    }

    // #292: HKLM\...\Schedule\Maintenance's exact value names aren't a documented, versioned
    // contract (much like BootTimeBreakdown's own boot-time component names) - this reads whatever
    // DWORD/string values are actually present under the key and reports them as a plain
    // label/value list rather than asserting fixed named properties this app can't guarantee.
    private const string MaintenancePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance";

    public static AutomaticMaintenanceInfo ReadAutomaticMaintenance()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MaintenancePath);
            if (key is null)
            {
                return new AutomaticMaintenanceInfo
                {
                    KeyPresent = false,
                    StatusText = "No Automatic Maintenance configuration key found on this system.",
                };
            }

            var settings = new List<PlatformLatencySettingRow>();
            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0) continue;
                object? raw = key.GetValue(name);
                settings.Add(new PlatformLatencySettingRow
                {
                    SettingName = name,
                    ValueText = raw?.ToString() ?? "Unknown",
                });
            }

            bool? disabled = settings.FirstOrDefault(s => s.SettingName.Equals("MaintenanceDisabled", StringComparison.OrdinalIgnoreCase)) is { } d && int.TryParse(d.ValueText, out int dv)
                ? dv != 0
                : null;

            return new AutomaticMaintenanceInfo
            {
                KeyPresent = true,
                StatusText = disabled switch
                {
                    true => "Automatic Maintenance is disabled by policy/configuration.",
                    false => "Automatic Maintenance is enabled. See the raw configuration below - live \"running right now\" state isn't exposed by this key; check the Scheduled Tasks Running list above for what's actually executing.",
                    null => $"{settings.Count} raw configuration value(s) found under this key.",
                },
                Settings = settings.OrderBy(s => s.SettingName, StringComparer.OrdinalIgnoreCase).ToList(),
            };
        }
        catch (Exception ex)
        {
            return new AutomaticMaintenanceInfo
            {
                KeyPresent = false,
                StatusText = $"Couldn't read Automatic Maintenance configuration: {ex.Message}",
            };
        }
    }

    private static string At(List<string> fields, int index) => index >= 0 && index < fields.Count ? fields[index] : string.Empty;

    public static async Task<(bool Success, string? Error)> SetEnabledAsync(string taskName, bool enabled)
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("schtasks.exe", $"/change /tn \"{taskName}\" /{(enabled ? "enable" : "disable")}");
            return exitCode == 0 ? (true, null) : (false, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// The logon-trigger delay ("Trigger: At log on, delay task for...") for a task - #80's
    /// actual measured value for a delayed-start app, as opposed to Task Manager's own estimated
    /// "startup impact" rating - plus (#23, Round 8) whether the task's Principal is configured to
    /// run whether the user is logged on or not, a distinct startup-impact category from an
    /// ordinary "only while I'm signed in" logon trigger since it can launch work even at the lock
    /// screen. Neither is exposed by `schtasks /query`'s CSV/table output at all - only the
    /// per-task XML export includes them, as an undocumented but stable
    /// &lt;Delay&gt;PT30S&lt;/Delay&gt; duration and &lt;LogonType&gt; element - so both are read
    /// together from one on-demand XML fetch per task (like Processes' module list or Services'
    /// recovery actions) rather than fetched for every row up front.
    /// </summary>
    public static async Task<(string DelayText, string RunModeText, string TriggerSummaryText)> ReadLogonTriggerInfoAsync(string taskName)
    {
        try
        {
            string xml = (await RunCapturedAsync("schtasks.exe", $"/query /tn \"{taskName}\" /xml")).Output;

            var delayMatch = Regex.Match(xml, @"<LogonTrigger>.*?<Delay>(P[^<]+)</Delay>", RegexOptions.Singleline);
            string delayText = delayMatch.Success
                ? $"Delay: {FormatIso8601Duration(delayMatch.Groups[1].Value)}"
                : "No logon-trigger delay configured.";

            var logonTypeMatch = Regex.Match(xml, @"<LogonType>([^<]+)</LogonType>");
            string runModeText = logonTypeMatch.Success ? DescribeLogonType(logonTypeMatch.Groups[1].Value) : "Unknown";

            // #767: the same XML fetch above also backs a full plain-language trigger summary -
            // reuses the raw `xml` string rather than a second schtasks /xml call ("share the
            // reader" between #80's delay/run-mode check and #767's fuller summary).
            string triggerSummaryText = BuildTriggerSummary(xml);

            return (delayText, runModeText, triggerSummaryText);
        }
        catch (Exception ex)
        {
            return ($"(couldn't read delay: {ex.Message})", "Unknown", $"(couldn't read trigger summary: {ex.Message})");
        }
    }

    /// <summary>
    /// #767: builds one plain-language sentence per trigger element in a task's XML export, beyond
    /// just LogonTrigger's delay - BootTrigger, TimeTrigger, CalendarTrigger, IdleTrigger,
    /// EventTrigger, RegistrationTrigger, plus the RandomDelay/ExecutionTimeLimit modifiers a
    /// trigger can carry and the RunOnlyIfIdle/StartWhenAvailable task-wide settings that change how
    /// every trigger behaves. Uses XDocument (like ListBootAndLogonTriggeredAsync's bulk parse)
    /// rather than the regex approach the delay/run-mode fields above use, since there are now
    /// several trigger element shapes to walk instead of one fixed pair of fields.
    /// </summary>
    private static string BuildTriggerSummary(string xml)
    {
        if (xml.Length == 0) return "(couldn't read this task's trigger configuration)";

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return "(couldn't parse this task's trigger XML)"; }
        if (doc.Root is null) return "(couldn't parse this task's trigger XML)";
        XNamespace ns = doc.Root.Name.Namespace;

        var triggersEl = doc.Root.Element(ns + "Triggers");
        var lines = new List<string>();

        if (triggersEl is null || !triggersEl.Elements().Any())
        {
            lines.Add("No triggers configured - this task only runs when started manually.");
        }
        else
        {
            foreach (var trigger in triggersEl.Elements())
            {
                string sentence = DescribeTrigger(trigger, ns);
                if (sentence.Length > 0) lines.Add(sentence);
            }
        }

        var settingsEl = doc.Root.Element(ns + "Settings");
        if (settingsEl?.Element(ns + "RunOnlyIfIdle")?.Value == "true")
            lines.Add("Only runs while the computer is idle.");
        if (settingsEl?.Element(ns + "StartWhenAvailable")?.Value == "true")
            lines.Add("A missed scheduled run is started as soon as possible once the computer becomes available.");

        return lines.Count == 0
            ? "This task has trigger configuration that couldn't be described in plain language."
            : string.Join("\n", lines);
    }

    private static string DescribeTrigger(XElement trigger, XNamespace ns)
    {
        string kind = trigger.Name.LocalName;
        string core = kind switch
        {
            "LogonTrigger" => "Runs at log on" + DescribeUserId(trigger, ns),
            "BootTrigger" => "Runs at system startup",
            "TimeTrigger" => "Runs once" + DescribeStartBoundary(trigger, ns),
            "CalendarTrigger" => "Runs on a schedule" + DescribeStartBoundary(trigger, ns) + DescribeCalendarRecurrence(trigger, ns),
            "IdleTrigger" => "Runs when the computer becomes idle",
            "EventTrigger" => "Runs in response to an event log entry" + DescribeEventSubscription(trigger, ns),
            "RegistrationTrigger" => "Runs when the task itself is created or modified",
            "SessionStateChangeTrigger" => "Runs on a session state change (lock/unlock/connect/disconnect)",
            _ => $"Has a {kind} trigger",
        };

        var modifiers = new List<string>();
        string? delay = trigger.Element(ns + "RandomDelay")?.Value;
        if (!string.IsNullOrEmpty(delay)) modifiers.Add($"randomized by up to {FormatIso8601Duration(delay)}");

        string? limit = trigger.Element(ns + "ExecutionTimeLimit")?.Value;
        if (!string.IsNullOrEmpty(limit)) modifiers.Add($"stopped if still running after {FormatIso8601Duration(limit)}");

        string modifierText = modifiers.Count > 0 ? " (" + string.Join(", ", modifiers) + ")" : string.Empty;
        string enabledSuffix = trigger.Element(ns + "Enabled")?.Value == "false" ? " - currently disabled" : string.Empty;
        return core + modifierText + enabledSuffix + ".";
    }

    private static string DescribeStartBoundary(XElement trigger, XNamespace ns)
    {
        var sb = trigger.Element(ns + "StartBoundary")?.Value;
        if (string.IsNullOrEmpty(sb)) return string.Empty;
        return DateTimeOffset.TryParse(sb, out var dt) ? $" starting {dt:g}" : string.Empty;
    }

    private static string DescribeUserId(XElement trigger, XNamespace ns)
    {
        var userId = trigger.Element(ns + "UserId")?.Value;
        return string.IsNullOrEmpty(userId) ? string.Empty : $" for {userId}";
    }

    private static string DescribeCalendarRecurrence(XElement trigger, XNamespace ns)
    {
        if (trigger.Element(ns + "ScheduleByDay") is not null) return ", daily";
        if (trigger.Element(ns + "ScheduleByWeek") is not null) return ", weekly";
        if (trigger.Element(ns + "ScheduleByMonth") is not null) return ", monthly";
        if (trigger.Element(ns + "ScheduleByMonthDayOfWeek") is not null) return ", monthly (by day of week)";
        return string.Empty;
    }

    private static string DescribeEventSubscription(XElement trigger, XNamespace ns)
    {
        var query = trigger.Element(ns + "Subscription")?.Value;
        return string.IsNullOrEmpty(query) ? string.Empty : $": {Truncate(query, 120)}";
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";

    /// <summary>Maps Task Scheduler's &lt;LogonType&gt; values to the same "only when logged on"
    /// vs. "whether or not logged on" distinction the Task Scheduler UI itself shows on a task's
    /// General tab - InteractiveToken is the "only when logged on" case; Password/S4U/
    /// ServiceAccount/Group all run independent of an interactive session.</summary>
    private static string DescribeLogonType(string logonType) => logonType switch
    {
        "InteractiveToken" => "Only when user is logged on",
        "InteractiveTokenOrPassword" => "Whether or not user is logged on",
        "Password" or "S4U" or "ServiceAccount" or "Group" => "Whether or not user is logged on",
        _ => $"Logon type: {logonType}",
    };

    /// <summary>Formats the simple PT#H#M#S shape Task Scheduler actually writes for a logon
    /// delay - not a general-purpose ISO-8601 duration parser.</summary>
    private static string FormatIso8601Duration(string iso)
    {
        var match = Regex.Match(iso, @"^PT(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?$");
        if (!match.Success) return iso;

        int hours = ParseGroup(match.Groups[1]);
        int minutes = ParseGroup(match.Groups[2]);
        int seconds = ParseGroup(match.Groups[3]);

        var parts = new List<string>();
        if (hours > 0) parts.Add($"{hours}h");
        if (minutes > 0) parts.Add($"{minutes}m");
        if (seconds > 0) parts.Add($"{seconds}s");
        return parts.Count == 0 ? "0s" : string.Join(" ", parts);
    }

    private static int ParseGroup(Group g) => g.Success && int.TryParse(g.Value, out int v) ? v : 0;

    /// #831: security-lens data schtasks' own CSV/table output never exposes at all - the per-task
    /// Hidden flag, registered folder, run-as identity, and action command. `schtasks /query /xml
    /// ONE` (no /tn) dumps every registered task as ONE aggregated well-formed XML document (each
    /// &lt;Task&gt; element under a &lt;Tasks&gt; root), so this is a single shell-out for the whole
    /// machine rather than one XML fetch per task the way ReadLogonTriggerInfoAsync above
    /// necessarily is for its single-task /query /tn /xml call. Parsed with XDocument rather than
    /// regex, since the output is genuinely well-formed XML here (unlike bitsadmin/fltmc's
    /// column-padded text). Wrapped in one broad try/catch - if the aggregate document doesn't
    /// parse (a stray console message ahead of the XML, or schtasks missing/failing entirely), the
    /// whole scan degrades to empty rather than partially succeeding, since there's no per-task
    /// boundary to recover at until the document itself is parsed.
    /// </summary>
    public static async Task<List<ScheduledTaskXmlInfo>> QuerySecurityInfoAsync()
    {
        var result = new List<ScheduledTaskXmlInfo>();
        try
        {
            // Hundreds of tasks each carrying a full XML definition can be a larger payload than
            // the other schtasks calls in this file - a longer timeout than the 10s default.
            string raw = (await RunCapturedAsync("schtasks.exe", "/query /xml ONE", timeoutMs: 20000)).Output;
            int start = raw.IndexOf("<Tasks", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return result; // unexpected output shape - degrade to empty
            var xml = raw[start..];

            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

            foreach (var taskEl in doc.Descendants(ns + "Task"))
            {
                try
                {
                    var uri = taskEl.Element(ns + "RegistrationInfo")?.Element(ns + "URI")?.Value;
                    if (string.IsNullOrWhiteSpace(uri)) continue;

                    bool isHidden = string.Equals(
                        taskEl.Element(ns + "Settings")?.Element(ns + "Hidden")?.Value,
                        "true", StringComparison.OrdinalIgnoreCase);

                    var principal = taskEl.Element(ns + "Principals")?.Elements(ns + "Principal")?.FirstOrDefault();
                    string runAsUser = principal?.Element(ns + "UserId")?.Value
                        ?? principal?.Element(ns + "GroupId")?.Value
                        ?? string.Empty;

                    var actionParts = new List<string>();
                    var actionsEl = taskEl.Element(ns + "Actions");
                    if (actionsEl is not null)
                    {
                        foreach (var exec in actionsEl.Elements(ns + "Exec"))
                        {
                            var cmd = exec.Element(ns + "Command")?.Value ?? string.Empty;
                            var args = exec.Element(ns + "Arguments")?.Value ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(cmd)) continue;
                            actionParts.Add(string.IsNullOrWhiteSpace(args) ? cmd : $"{cmd} {args}");
                        }
                    }

                    int lastSlash = uri.LastIndexOf('\\');
                    string folderPath = lastSlash > 0 ? uri[..lastSlash] : @"\";

                    result.Add(new ScheduledTaskXmlInfo
                    {
                        Name = uri,
                        FolderPath = folderPath,
                        IsHidden = isHidden,
                        RunAsUser = runAsUser,
                        ActionCommand = string.Join(" ; ", actionParts),
                    });
                }
                catch
                {
                    // One malformed <Task> block shouldn't stop the rest.
                }
            }
        }
        catch
        {
            // schtasks unavailable/failed/timed out, or the aggregate document didn't parse -
            // degrade to empty, same as every other optional data source in this app.
        }
        return result;
    }

    /// <summary>
    /// #747: boot- and logon-triggered scheduled tasks, folded into the Startup tab's main grid as
    /// first-class rows. Reads one combined `schtasks /query /xml ONE` export (every registered
    /// task's XML in one shelled-out call) rather than fetching each task's individual XML - the
    /// same "one shared read, not N per-item reads" tradeoff BcdInspectorService's single
    /// ReadAsync() snapshot already takes, important here since a system can have hundreds of
    /// tasks. `/xml ONE` concatenates one `&lt;?xml ...?&gt;&lt;Task&gt;...&lt;/Task&gt;` document
    /// per task back to back rather than wrapping them in one shared root - not valid XML as a
    /// whole, so this splits on each XML declaration and parses one task fragment at a time (a
    /// malformed fragment is skipped rather than failing the whole scan).
    /// </summary>
    public static async Task<List<ScheduledTaskTriggerInfo>> ListBootAndLogonTriggeredAsync()
    {
        var result = new List<ScheduledTaskTriggerInfo>();
        try
        {
            string xml = (await RunCapturedAsync("schtasks.exe", "/query /xml ONE", timeoutMs: 20000)).Output;
            if (xml.Length == 0) return result;

            foreach (var block in SplitXmlDeclarations(xml))
            {
                XDocument doc;
                try { doc = XDocument.Parse(block); }
                catch { continue; } // one malformed fragment shouldn't drop the rest of the scan

                if (doc.Root is null) continue;
                XNamespace ns = doc.Root.Name.Namespace;

                // `/xml ONE`'s actual wrapping shape (one root `<Task>` per declaration, vs. all
                // tasks nested under one `<Tasks>` root) isn't a documented, versioned contract, so
                // this handles either: if the root itself is a Task, use it directly; otherwise
                // look for `<Task>` descendants.
                var taskElements = doc.Root.Name.LocalName == "Task"
                    ? new[] { doc.Root }
                    : doc.Descendants(ns + "Task").ToArray();

                foreach (var taskEl in taskElements)
                {
                    try
                    {
                        bool hasBoot = taskEl.Descendants(ns + "BootTrigger").Any();
                        bool hasLogon = taskEl.Descendants(ns + "LogonTrigger").Any();
                        if (!hasBoot && !hasLogon) continue;

                        string uri = taskEl.Descendants(ns + "RegistrationInfo").FirstOrDefault()?.Element(ns + "URI")?.Value
                            ?? "(unknown task)";

                        var exec = taskEl.Descendants(ns + "Exec").FirstOrDefault();
                        string command = exec?.Element(ns + "Command")?.Value ?? string.Empty;
                        string args = exec?.Element(ns + "Arguments")?.Value ?? string.Empty;
                        string fullCommand = args.Length > 0 ? $"{command} {args}" : command;

                        bool enabled = taskEl.Descendants(ns + "Settings").FirstOrDefault()?.Element(ns + "Enabled")?.Value != "false";

                        result.Add(new ScheduledTaskTriggerInfo
                        {
                            TaskName = uri,
                            Command = fullCommand,
                            HasBootTrigger = hasBoot,
                            HasLogonTrigger = hasLogon,
                            IsEnabled = enabled,
                        });
                    }
                    catch { /* one task's fragment shouldn't drop the rest */ }
                }
            }
        }
        catch
        {
            // schtasks unavailable/failed - empty list, same degrade-on-failure pattern as ListAsync.
        }
        return result.OrderBy(t => t.TaskName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Splits schtasks' `/xml ONE` output on each `&lt;?xml` declaration - see
    /// ListBootAndLogonTriggeredAsync's remarks for why the raw output isn't one well-formed
    /// document. Returns the whole string as a single block when there's zero or one declaration
    /// (nothing to split, or a single well-formed document).</summary>
    private static List<string> SplitXmlDeclarations(string combined)
    {
        var starts = new List<int>();
        int idx = 0;
        while ((idx = combined.IndexOf("<?xml", idx, StringComparison.Ordinal)) >= 0)
        {
            starts.Add(idx);
            idx += 5;
        }
        if (starts.Count == 0) return new List<string> { combined };

        var blocks = new List<string>(starts.Count);
        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i];
            int end = i + 1 < starts.Count ? starts[i + 1] : combined.Length;
            blocks.Add(combined[start..end]);
        }
        return blocks;
    }

    /// <summary>
    /// #655: wake-enabled ("Wake the computer to run this task") scheduled tasks - the other
    /// software wake cause shown alongside WakeTimerService's active wake timers.
    /// `schtasks /query /xml` with no /tn exports *every* registered task as a series of
    /// back-to-back `&lt;?xml ...?&gt;&lt;Task ...&gt;...&lt;/Task&gt;` documents, not one
    /// well-formed document - confirmed live on a real dev machine (189 concatenated `&lt;?xml`
    /// declarations for 189 tasks), a different quirk from the single-task
    /// ReadLogonTriggerInfoAsync above - so this splits on that declaration boundary and parses
    /// each fragment independently. WakeToRun is a documented, stable part of the Task Scheduler
    /// XML schema (unlike most of what this app parses out of tool text output), also confirmed
    /// live against a real wake-enabled task's exported XML.
    /// </summary>
    public static async Task<List<ScheduledTaskRow>> ListWakeEnabledAsync()
    {
        var result = new List<ScheduledTaskRow>();
        try
        {
            string output = (await RunCapturedAsync("schtasks.exe", "/query /xml", 30000)).Output;
            foreach (var fragment in SplitXmlDocuments(output))
            {
                XDocument doc;
                try { doc = XDocument.Parse(fragment); } catch { continue; }

                var settings = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Settings");
                bool wakeToRun = settings?.Elements().FirstOrDefault(e => e.Name.LocalName == "WakeToRun")?.Value
                    .Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
                if (!wakeToRun) continue;

                string name = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "URI")?.Value.Trim() ?? string.Empty;
                if (name.Length == 0) continue;

                bool enabled = settings?.Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled")?.Value
                    .Equals("true", StringComparison.OrdinalIgnoreCase) ?? true;

                result.Add(new ScheduledTaskRow { Name = name, Status = enabled ? "Ready" : "Disabled", IsEnabled = enabled });
            }
        }
        catch
        {
            // schtasks unavailable/failed - empty list, same degrade-on-failure convention as ListAsync.
        }
        return result.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> SplitXmlDocuments(string output)
    {
        var docs = new List<string>();
        int idx = output.IndexOf("<?xml", StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            int next = output.IndexOf("<?xml", idx + 5, StringComparison.OrdinalIgnoreCase);
            docs.Add(next >= 0 ? output[idx..next] : output[idx..]);
            idx = next;
        }
        return docs;
    }

    /// <summary>
    /// Shells out and captures combined stdout+stderr, bounded by a real timeout - the same
    /// concurrent-read/bounded-wait/kill-on-timeout pattern TracerouteService.RunAsync already
    /// established. The previous version read both streams synchronously to completion *before*
    /// waiting for exit at all (the classic .NET Process redirection deadlock: both streams' OS
    /// pipe buffers are small and fixed-size, so a child that fills one while nothing drains it
    /// blocks forever, and the parent blocks reading right alongside it), then read
    /// `proc.WaitForExit(10000)`'s bool result without checking it - so a process that legitimately
    /// ran past 10s made the later `proc.ExitCode` read throw InvalidOperationException ("Process
    /// must exit before requested information can be determined"), which every caller here
    /// (List/SetEnabled/ReadLogonTriggerInfo) would have seen as an unexpected exception rather
    /// than a clean "no output" result. A timed-out run now returns ExitCode: null instead, so
    /// callers treat it exactly like any other non-zero/empty result already handled below.
    /// </summary>
    private static async Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 10000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { /* best-effort */ }
            return ("(command timed out)", null);
        }

        string output = (await outputTask) + (await errorTask);
        return (output, proc.ExitCode);
    }

    private static List<string> SplitCsvLines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();

    // schtasks' CSV output quotes every field and escapes an embedded quote by doubling it ("") -
    // a small hand-rolled parser rather than a dependency, since this app takes no CSV library
    // anywhere else and the escaping rule here is simple and fixed.
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }

    #region #765 - Task last-result decoder

    private static readonly Dictionary<string, string> KnownLastResultCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0x0"] = "Success",
        ["0x41300"] = "Task is ready to run",
        ["0x41301"] = "Task is currently running",
        ["0x41303"] = "Task has not yet run",
        ["0x41306"] = "Task was terminated by the user",
        ["0x8004131F"] = "An instance of this task is already running",
        ["0x80070002"] = "The system cannot find the file specified",
        ["0x800704DD"] = "No mapped/logged-on user for this operation",
    };

    /// <summary>#765: decodes the common hex "Last Result" codes schtasks reports inline,
    /// synchronously, with no shell-out - see DecodeLastResultAsync for the certutil fallback used
    /// for anything not in this small known-codes table.</summary>
    public static string? DecodeKnownLastResult(string lastResult)
    {
        string trimmed = lastResult.Trim();
        return trimmed.Length > 0 && KnownLastResultCodes.TryGetValue(trimmed, out var known) ? known : null;
    }

    /// <summary>#765: falls back to `certutil -error &lt;code&gt;` for any Last Result code not in
    /// the small known-codes table above - certutil's own generic Win32/HRESULT-to-text lookup, the
    /// same "known Windows tool" tradeoff this file already takes for schtasks/Task Scheduler
    /// itself. On-demand only (DecodeLastResultCommand, for the selected row), not run for every row
    /// up front.</summary>
    public static async Task<string> DecodeLastResultAsync(string lastResult)
    {
        if (DecodeKnownLastResult(lastResult) is { } known) return known;

        string trimmed = lastResult.Trim();
        if (trimmed.Length == 0) return "Unknown";

        try
        {
            var (output, exitCode) = await RunCapturedAsync("certutil.exe", $"-error {trimmed}");
            var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return exitCode == 0 && line is { Length: > 0 } ? line : "Unknown error code";
        }
        catch (Exception ex)
        {
            return $"(couldn't decode: {ex.Message})";
        }
    }

    #endregion

    #region #766 - Tasks with a missing target

    /// <summary>
    /// #766: resolves a task's "Task To Run" command line down to its target executable the same
    /// way StartupManagerService.ExtractPath already does for the Startup tab's own registry Run
    /// entries (quoted-path / first-space-before-.exe heuristic), then checks whether that resolved
    /// file actually exists - flags an uninstalled app, a deleted user profile path, or an
    /// unreachable network share the same way (File.Exists returns false for all three). A
    /// genuinely unreachable UNC share degrades to "unknown" (never flagged as missing) via a
    /// bounded timeout, rather than blocking the whole scan on a slow/offline share and rather than
    /// fabricating a missing verdict for a share that simply couldn't be reached in time.
    /// </summary>
    public static (bool HasMissingTarget, string Reason) EvaluateTarget(string taskToRun)
    {
        if (string.IsNullOrWhiteSpace(taskToRun)) return (false, string.Empty);

        string path = StartupManagerService.ExtractPath(taskToRun);
        if (path.Length == 0) return (false, string.Empty);

        string expanded = Environment.ExpandEnvironmentVariables(path);

        // A bare executable name with no directory component (e.g. "cmd.exe") is meant to resolve
        // via the OS's own PATH search at launch time, not relative to this app's own working
        // directory - checking File.Exists on the bare name alone would false-positive on every
        // perfectly valid task that just names a well-known tool. Try PATH resolution first for
        // that shape; only fall through to the plain existence check once a directory is present.
        if (Path.GetDirectoryName(expanded) is { Length: 0 } or null && Path.GetFileName(expanded) == expanded)
        {
            if (ResolveViaPath(expanded) is not null) return (false, string.Empty);
        }

        bool? exists = FileExistsWithTimeout(expanded);
        if (exists is not false) return (false, string.Empty); // true, or null ("couldn't confirm in time") - never fabricated as missing

        string reason = expanded.StartsWith(@"\\", StringComparison.Ordinal)
            ? $"Points at a network path that couldn't be found: {expanded}"
            : $"Points at a file that no longer exists: {expanded}";
        return (true, reason);
    }

    /// <summary>Resolves a bare executable name (no directory component) against System32,
    /// Windows, and the PATH environment variable, the same search Windows itself performs when
    /// launching a program named without a path - so "cmd.exe"/"notepad.exe"/etc never
    /// false-positive as missing.</summary>
    private static string? ResolveViaPath(string fileName)
    {
        try
        {
            var candidateDirs = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System)),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            };
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (pathEnv is not null) candidateDirs.AddRange(pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries));

            foreach (var dir in candidateDirs)
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch
        {
            // Malformed PATH entry or similar - fall through to "couldn't resolve", the caller
            // then falls back to the plain File.Exists check on the bare name.
        }
        return null;
    }

    private static bool? FileExistsWithTimeout(string path, int timeoutMs = 2000)
    {
        try
        {
            var task = Task.Run(() => File.Exists(path));
            return task.Wait(timeoutMs) ? task.Result : null;
        }
        catch
        {
            return null;
        }
    }

    #endregion
}
