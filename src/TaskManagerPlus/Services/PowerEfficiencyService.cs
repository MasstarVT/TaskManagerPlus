using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #660: runs the 60-second `powercfg /energy` diagnostic trace and parses its HTML report into a
/// typed Errors/Warnings/Information findings list. This is a genuinely different tier of
/// "on-demand" from every other powercfg call in this app - it deliberately keeps the system busy
/// for about a minute to observe idle-state and device-power behavior, so it must only ever run
/// from an explicit button (never at startup, never on a timer - CLAUDE.md's on-demand-vs-polled
/// convention calls this out by name).
///
/// The report's HTML layout is not a documented, versioned Microsoft contract (same caveat every
/// other powercfg text/XML parse in this app already carries - see PowerPlanService's remarks),
/// and unlike `/qh`'s stable "(Friendly Name)" bracket convention this file's other parsers lean
/// on, the `/energy` report's markup has actually changed between Windows releases. Rather than
/// bind to one specific table/row shape, each finding is read generically: every
/// &lt;div class="error|warning|information"&gt; block in the report becomes one finding, with its
/// inner text (HTML-stripped) split into a title (the block's first line) and detail (the rest).
/// That's robust to markup drift in a way a fixed-column table parse wouldn't be - if a given
/// Windows build wraps findings in some other structure entirely, this comes back with zero
/// findings and a status text that still points the user at the raw report file, never a
/// fabricated result.
/// </summary>
public static class PowerEfficiencyService
{
    private static readonly Regex SeverityDivRegex = new(
        @"<div[^>]*\bclass\s*=\s*[""'](?<sev>error|warning|information)[""'][^>]*>(?<body>.*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"[ \t]+", RegexOptions.Compiled);

    public static async Task<(List<PowerEfficiencyFinding> Findings, string StatusText, string? ReportPath)> RunScanAsync(
        IProgress<string>? progress = null)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "TaskManagerPlus");
        string reportPath = Path.Combine(tempDir, $"energy-report-{Guid.NewGuid():N}.html");
        try
        {
            Directory.CreateDirectory(tempDir);
            progress?.Report("Running powercfg /energy - this takes about 60 seconds...");

            // 60s trace + generous margin for powercfg's own startup/report-write time, not the
            // usual ~10-20s timeout the rest of this app's shelled-out reads use.
            var (output, exitCode) = await RunProcessAsync("powercfg.exe", $"/energy /output \"{reportPath}\" /duration 60", 100000);

            if (!File.Exists(reportPath))
                return (new List<PowerEfficiencyFinding>(), $"powercfg /energy didn't produce a report: {Truncate(output.Trim(), 300)}", null);

            string html = await File.ReadAllTextAsync(reportPath);
            var findings = ParseReport(html);

            string status;
            if (findings.Count > 0)
            {
                int errors = findings.Count(f => f.Severity == "Error");
                int warnings = findings.Count(f => f.Severity == "Warning");
                int info = findings.Count(f => f.Severity == "Information");
                status = $"{errors} error(s), {warnings} warning(s), {info} informational finding(s). Full report: {reportPath}";
            }
            else
            {
                status = exitCode == 0
                    ? $"Report generated but no findings could be parsed from it (its layout may differ on this Windows build) - open it directly: {reportPath}"
                    : $"powercfg /energy exited with an error: {Truncate(output.Trim(), 300)}";
            }

            return (findings, status, reportPath);
        }
        catch (Exception ex)
        {
            return (new List<PowerEfficiencyFinding>(), $"Couldn't run powercfg /energy: {ex.Message}", null);
        }
    }

    internal static List<PowerEfficiencyFinding> ParseReport(string html)
    {
        var result = new List<PowerEfficiencyFinding>();
        foreach (Match m in SeverityDivRegex.Matches(html))
        {
            string severity = m.Groups["sev"].Value switch
            {
                "error" => "Error",
                "warning" => "Warning",
                _ => "Information",
            };

            string text = StripHtml(m.Groups["body"].Value);
            if (text.Length == 0) continue;

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0) continue;

            string title = Truncate(lines[0], 220);
            string detail = lines.Length > 1 ? Truncate(string.Join(" | ", lines.Skip(1)), 500) : string.Empty;

            result.Add(new PowerEfficiencyFinding { Severity = severity, Title = title, Detail = detail });
        }
        return result;
    }

    private static string StripHtml(string fragment)
    {
        // Table/row/cell boundaries in the source become line breaks so StripHtml's caller can
        // still separate a title from its following detail rows once tags are gone.
        string withBreaks = Regex.Replace(fragment, @"</(tr|table|p|div|h\d)>", "\n", RegexOptions.IgnoreCase);
        string noTags = TagRegex.Replace(withBreaks, " ");
        string decoded = System.Net.WebUtility.HtmlDecode(noTags);
        string collapsed = WhitespaceRegex.Replace(decoded, " ");
        return string.Join('\n', collapsed.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's historical non-nullable shape (-1 for a
    /// timed-out run).</summary>
    private static async Task<(string Output, int ExitCode)> RunProcessAsync(string exe, string args, int timeoutMs)
    {
        var (output, exitCode) = await ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
        return (output, exitCode ?? -1);
    }
}
