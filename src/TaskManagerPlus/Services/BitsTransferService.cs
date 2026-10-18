using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #291: lists active BITS (Background Intelligent Transfer Service) jobs by shelling out to
/// `bitsadmin.exe /list /allusers /verbose` and parsing its text output - the same "known Windows
/// tool over raw interop" tradeoff every other shelled-out reader in this app takes (there is a
/// BITS COM API, but no service in this app takes a COM interop dependency anywhere else). Exact
/// field indentation/spacing in bitsadmin's output isn't a documented, versioned contract, so this
/// parses loosely (a "LABEL: value" line scan per job block, the same tolerant style
/// ScheduledTaskService/WifiScanStormService already use for other CLI tools) rather than assuming
/// fixed column positions. On-demand-refreshed-plus-startup-load per CLAUDE.md's tiering for a
/// cheap-ish shell-out.
/// </summary>
public static class BitsTransferService
{
    private static readonly Regex BytesRegex = new(@"(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);

    public static async Task<(bool Success, string Message, List<BitsTransferRow> Rows)> ListAsync()
    {
        string output;
        int? exitCode;
        try
        {
            (output, exitCode) = await RunCapturedAsync("bitsadmin.exe", "/list /allusers /verbose");
        }
        catch (Exception ex)
        {
            return (false, $"Couldn't run bitsadmin.exe: {ex.Message}", new List<BitsTransferRow>());
        }

        if (exitCode is null)
            return (false, "bitsadmin.exe timed out.", new List<BitsTransferRow>());

        var rows = ParseVerboseOutput(output);

        // bitsadmin returns 0 whether or not there are jobs - an empty parse just means "no active
        // BITS transfers right now", the common case, not a failure.
        string message = rows.Count == 0
            ? "No active BITS transfers found."
            : $"{rows.Count} active BITS transfer(s).";
        return (true, message, rows);
    }

    private static List<BitsTransferRow> ParseVerboseOutput(string output)
    {
        var rows = new List<BitsTransferRow>();
        foreach (var job in ParseJobBlocks(output))
        {
            var f = job.Fields;
            long bytesTransferred = 0, bytesTotal = 0;
            if (f.TryGetValue("BYTES", out var bytes) && BytesRegex.Match(bytes) is { Success: true } m)
            {
                long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out bytesTransferred);
                long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out bytesTotal);
            }
            rows.Add(new BitsTransferRow
            {
                DisplayName = f.TryGetValue("DISPLAY", out var display) ? display : "(unnamed)",
                JobId = f.TryGetValue("GUID", out var guid) ? guid : string.Empty,
                TypeText = f.TryGetValue("TYPE", out var type) ? type : "Unknown",
                StateText = f.TryGetValue("STATE", out var state) ? state : "Unknown",
                Owner = f.TryGetValue("OWNER", out var owner) ? owner : string.Empty,
                PriorityText = f.TryGetValue("PRIORITY", out var priority) ? priority : "Unknown",
                BytesTransferred = bytesTransferred,
                BytesTotal = bytesTotal,
            });
        }
        return rows;
    }

    /// <summary>One job from `bitsadmin /list /verbose`: its LABEL: value fields plus the
    /// "remote -> local" pairs listed under JOB FILES.</summary>
    internal sealed class BitsJobBlock
    {
        public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Remote, string Local)> Files { get; } = new();
    }

    // bitsadmin packs several fields onto one line ("GUID: {..} DISPLAY: 'name'", "TYPE: DOWNLOAD
    // STATE: SUSPENDED OWNER: PC\user", "PRIORITY: NORMAL FILES: 0 / 1 BYTES: 0 / UNKNOWN"), so a
    // value runs until the next known label rather than to the end of the line. Upper-case only,
    // as bitsadmin prints them; "JOB ID" is accepted as a GUID alias.
    private static readonly Regex JobLabelRegex = new(
        @"(?<![A-Za-z0-9])(GUID|JOB ID|DISPLAY|TYPE|STATE|OWNER|PRIORITY|FILES|BYTES|CREATION TIME|MODIFICATION TIME|COMPLETION TIME|ACL FLAGS|NOTIFY INTERFACE|NOTIFICATION FLAGS|RETRY DELAY|NO PROGRESS TIMEOUT|ERROR COUNT|ERROR FILE|ERROR CODE|ERROR CONTEXT|PROXY USAGE|PROXY LIST|PROXY BYPASS LIST|DESCRIPTION|JOB FILES|NOTIFICATION COMMAND LINE)\s*:",
        RegexOptions.Compiled);

    private static readonly Regex JobFileRegex = new(@"(\S+)\s+->\s+(.+)$", RegexOptions.Compiled);

    /// <summary>A GUID label (or a DISPLAY label when the current job already has one) starts a
    /// new job, so both the one-line "GUID: .. DISPLAY: .." shape and a DISPLAY-first shape parse.
    /// Text before the first job (the banner) is ignored.</summary>
    internal static List<BitsJobBlock> ParseJobBlocks(string output)
    {
        var jobs = new List<BitsJobBlock>();
        BitsJobBlock? current = null;

        foreach (var raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var labels = JobLabelRegex.Matches(line);
            var lineFields = new List<(string Label, string Value)>();
            for (int i = 0; i < labels.Count; i++)
            {
                int valueStart = labels[i].Index + labels[i].Length;
                int valueEnd = i + 1 < labels.Count ? labels[i + 1].Index : line.Length;
                string label = labels[i].Groups[1].Value == "JOB ID" ? "GUID" : labels[i].Groups[1].Value;
                lineFields.Add((label, line[valueStart..valueEnd].Trim().Trim('\'', '"').Trim()));
            }

            bool startsJob = lineFields.Any(lf => lf.Label == "GUID")
                || (lineFields.Any(lf => lf.Label == "DISPLAY") && (current is null || current.Fields.ContainsKey("DISPLAY")));
            if (startsJob)
            {
                current = new BitsJobBlock();
                jobs.Add(current);
            }
            if (current is null) continue;

            foreach (var (label, value) in lineFields)
                if (value.Length > 0) current.Fields.TryAdd(label, value); // first occurrence wins

            // JOB FILES rows: "<bytes> / <total> <STATE> <remote> -> <local>" (ERROR FILE uses the same arrow).
            if (line.Contains(" -> ", StringComparison.Ordinal) && JobFileRegex.Match(line) is { Success: true } fm)
                current.Files.Add((fm.Groups[1].Value, fm.Groups[2].Value.Trim()));
        }
        return jobs;
    }

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's historical default timeout.</summary>
    private static Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 15000)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
}
