using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
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
        return rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
    public static async Task<(string DelayText, string RunModeText)> ReadLogonTriggerInfoAsync(string taskName)
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

            return (delayText, runModeText);
        }
        catch (Exception ex)
        {
            return ($"(couldn't read delay: {ex.Message})", "Unknown");
        }
    }

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
}
