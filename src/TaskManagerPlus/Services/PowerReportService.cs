using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #226/#229/#231: shared "run powercfg with an HTML report output, wait for it, scrape specific
/// sections out of the HTML" helper - all three features share this exact shape (per CLAUDE.md's
/// "known tool, text/HTML output parsed" tradeoff, same tier as
/// PowerSchemeInterruptSteeringService's powercfg -q parse) even though their three parsers stay
/// distinct. No HtmlAgilityPack/XML dependency is taken for this - powercfg's report HTML is (per
/// the task's own framing: "a stable, long-documented Microsoft format") simple enough that a
/// tolerant regex-based table-row scrape is enough, matching this whole chunk's "best-effort, skip
/// a row you can't confidently parse rather than guessing" mandate; a Windows-build HTML layout
/// change just means fewer/no rows parse, not a crash or a fabricated row.
///
/// #226/#229 both run `powercfg /energy` - deliberately two separate on-demand runs rather than
/// one shared invocation: #226 ("who raised the timer resolution") uses a short 15s duration for a
/// quick, focused answer, while #229 (the full "Errors/Warnings" report) uses Microsoft's own
/// documented default of 60s, since a shorter run is documented to reduce the reliability of some
/// findings. Folding them into one run would force every "who raised the timer resolution?" click
/// into a 60s wait for a question that doesn't need it.
/// </summary>
public static class PowerReportService
{
    private static readonly Regex RowRegex = new(@"<tr[^>]*\bclass\s*=\s*[""'](?:error|warning)[""'][^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AnyRowRegex = new(@"<tr[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CellRegex = new(@"<td[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ExeNameRegex = new(@"[A-Za-z0-9_.\-]+\.exe\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string ReportsDir => Path.Combine(AppPaths.SettingsDirectory, "PowerReports");

    /// <summary>#229: the full Errors/Warnings scan - Microsoft's documented default duration
    /// (60s). Findings are every parsed &lt;tr class="error"|"warning"&gt; row's cell text, most
    /// severe (error) first.</summary>
    public static async Task<(bool Ok, string Message, string? ReportPath, List<EnergyReportFinding> Findings)> RunEnergyReportAsync(CancellationToken ct)
    {
        string path = Path.Combine(ReportsDir, $"energy_{DateTime.Now:yyyyMMdd_HHmmss}.html");
        var (produced, output) = await RunToFileAsync($"/energy /duration 60 /output \"{path}\"", path, TimeSpan.FromSeconds(90), ct);
        if (!produced)
            return (false, $"powercfg /energy didn't produce a report: {Truncate(output)}", null, new List<EnergyReportFinding>());

        string html = await ReadAllTextSafeAsync(path);
        var findings = ParseFindings(html);
        return (true, findings.Count == 0
            ? "Report complete - no Errors/Warnings rows were found (or the report's HTML layout didn't match this build's parser)."
            : $"Report complete - {findings.Count} finding(s).", path, findings);
    }

    /// <summary>#226: a shorter, separate 15s /energy run focused on just the "who's holding a
    /// raised timer resolution" question - see the class remarks for why this isn't folded into
    /// #229's 60s run.</summary>
    public static async Task<(bool Ok, string Message, List<TimerResolutionRequesterRow> Rows)> RunTimerResolutionRequestersAsync(CancellationToken ct)
    {
        string path = Path.Combine(ReportsDir, $"timerres_{DateTime.Now:yyyyMMdd_HHmmss}.html");
        var (produced, output) = await RunToFileAsync($"/energy /duration 15 /output \"{path}\"", path, TimeSpan.FromSeconds(45), ct);
        if (!produced)
            return (false, $"powercfg /energy didn't produce a report: {Truncate(output)}", new List<TimerResolutionRequesterRow>());

        string html = await ReadAllTextSafeAsync(path);
        var findings = ParseFindings(html)
            .Where(f => f.Description.Contains("Timer Resolution", StringComparison.OrdinalIgnoreCase)
                     || f.Detail.Contains("Timer Resolution", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var rows = new List<TimerResolutionRequesterRow>();
        foreach (var f in findings)
        {
            string combined = f.Description + " " + f.Detail;
            var exeNames = ExeNameRegex.Matches(combined).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (exeNames.Count == 0)
            {
                rows.Add(new TimerResolutionRequesterRow { ProcessName = "(not named in report text)", Detail = Truncate(f.Detail.Length > 0 ? f.Detail : f.Description) });
            }
            else
            {
                foreach (var name in exeNames)
                    rows.Add(new TimerResolutionRequesterRow { ProcessName = name, Detail = "From the powercfg /energy report." });
            }
        }
        return (true, rows.Count == 0 ? "15s /energy scan found no outstanding timer-resolution requests." : $"{rows.Count} requester(s) found in the /energy report.", rows);
    }

    /// <summary>#231: powercfg /sleepstudy, falling back to /systemsleepdiagnostics when
    /// sleepstudy's own output/exit code indicates it isn't supported on this build/hardware (both
    /// are the same "run + HTML-scrape" shape, just a different Microsoft report command). Callers
    /// should only invoke this after confirming modern-standby support (see
    /// ResponsivenessViewModel.ModernStandbySupported) - this method itself doesn't re-check that.</summary>
    public static async Task<(bool Ok, string Message, string? ReportPath, List<SleepStudyActivatorRow> Activators)> RunSleepStudyAsync(CancellationToken ct)
    {
        string path = Path.Combine(ReportsDir, $"sleepstudy_{DateTime.Now:yyyyMMdd_HHmmss}.html");
        var (produced, output) = await RunToFileAsync($"/sleepstudy /output \"{path}\"", path, TimeSpan.FromSeconds(60), ct);
        string command = "powercfg /sleepstudy";

        if (!produced)
        {
            // /sleepstudy is unsupported on some builds/hardware (no modern-standby history yet,
            // older report format, etc.) - fall back to the newer /systemsleepdiagnostics report.
            path = Path.Combine(ReportsDir, $"sleepdiag_{DateTime.Now:yyyyMMdd_HHmmss}.html");
            (produced, output) = await RunToFileAsync($"/systemsleepdiagnostics /output \"{path}\"", path, TimeSpan.FromSeconds(60), ct);
            command = "powercfg /systemsleepdiagnostics";
        }

        if (!produced)
            return (false, $"Neither /sleepstudy nor /systemsleepdiagnostics produced a report: {Truncate(output)}", null, new List<SleepStudyActivatorRow>());

        string html = await ReadAllTextSafeAsync(path);
        var activators = ParseActivators(html);
        return (true, activators.Count == 0
            ? $"{command} completed, but no activator rows were found (or this build's report HTML layout didn't match this parser)."
            : $"{command} completed - top {activators.Count} activator(s).", path, activators);
    }

    /// <summary>#226: best-effort read of the newer, less-universal
    /// HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\GlobalTimerResolutionRequests
    /// registry value - an undocumented REG_BINARY blob some Windows builds use to track which
    /// processes currently hold a raised timer-resolution request. Its exact structure isn't
    /// publicly documented, so this deliberately doesn't try to fully decode it (that would risk
    /// presenting a guessed field as real, against this app's "never fabricate" rule) - it only
    /// scans the raw bytes for UTF-16LE substrings that look like a process name
    /// ("...something.exe"), which is enough to name a likely requester without claiming to fully
    /// understand the format. ValuePresent is false (never a guess) when the key/value doesn't
    /// exist on this Windows build.</summary>
    public static (bool ValuePresent, List<TimerResolutionRequesterRow> Rows) ReadGlobalTimerResolutionRequestsFromRegistry()
    {
        var rows = new List<TimerResolutionRequesterRow>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel");
            if (key?.GetValue("GlobalTimerResolutionRequests") is not byte[] data || data.Length == 0)
                return (false, rows);

            string text = System.Text.Encoding.Unicode.GetString(data);
            foreach (Match m in ExeNameRegex.Matches(text))
            {
                string name = m.Value;
                if (rows.Any(r => r.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                rows.Add(new TimerResolutionRequesterRow { ProcessName = name, Detail = "From the GlobalTimerResolutionRequests registry value (undocumented format, best-effort name scan)." });
            }
            return (true, rows);
        }
        catch
        {
            return (false, rows);
        }
    }

    private static List<EnergyReportFinding> ParseFindings(string html)
    {
        var findings = new List<EnergyReportFinding>();
        if (string.IsNullOrWhiteSpace(html)) return findings;

        foreach (Match rowMatch in RowRegex.Matches(html))
        {
            var cells = CellRegex.Matches(rowMatch.Groups[1].Value)
                .Select(m => StripHtml(m.Groups[1].Value))
                .Where(t => t.Length > 0)
                .ToList();
            if (cells.Count == 0) continue; // tolerant: skip a row we can't confidently read, never guess.

            string severity = Regex.IsMatch(rowMatch.Value, @"class\s*=\s*[""']error[""']", RegexOptions.IgnoreCase) ? "Error" : "Warning";

            findings.Add(new EnergyReportFinding
            {
                Severity = severity,
                Description = cells[0],
                Detail = cells.Count > 1 ? string.Join(" — ", cells.Skip(1)) : string.Empty,
            });
        }
        return findings.OrderBy(f => f.Severity == "Error" ? 0 : 1).ToList();
    }

    /// <summary>#231: tolerant scrape for a "top activators" style table - the sleepstudy/
    /// systemsleepdiagnostics report's exact structure isn't a stable documented contract the way
    /// the /energy Errors/Warnings table is, so this looks for the nearest table rows following any
    /// heading/text containing "Activator" rather than assuming a fixed layout, and caps the
    /// result so an unrelated table further down the report doesn't get scraped as activators.</summary>
    private static List<SleepStudyActivatorRow> ParseActivators(string html)
    {
        var rows = new List<SleepStudyActivatorRow>();
        if (string.IsNullOrWhiteSpace(html)) return rows;

        int idx = html.IndexOf("Activator", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return rows;

        string section = html[idx..Math.Min(html.Length, idx + 40000)];

        foreach (Match rowMatch in AnyRowRegex.Matches(section))
        {
            var cells = CellRegex.Matches(rowMatch.Groups[1].Value)
                .Select(m => StripHtml(m.Groups[1].Value))
                .Where(t => t.Length > 0)
                .ToList();
            if (cells.Count == 0) continue;
            // Skip an obvious lone column-header cell (e.g. just "Activator").
            if (cells.Count == 1 && cells[0].Length < 20 && cells[0].Equals("Activator", StringComparison.OrdinalIgnoreCase)) continue;

            rows.Add(new SleepStudyActivatorRow
            {
                Name = cells[0],
                Detail = cells.Count > 1 ? string.Join(" — ", cells.Skip(1)) : string.Empty,
            });
            if (rows.Count >= 15) break; // top activators only.
        }
        return rows;
    }

    private static string StripHtml(string s) =>
        System.Net.WebUtility.HtmlDecode(TagRegex.Replace(s, " ")).Replace(' ', ' ').Trim();

    private static string Truncate(string s, int max = 300) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "…");

    private static async Task<string> ReadAllTextSafeAsync(string path)
    {
        try { return await File.ReadAllTextAsync(path); }
        catch { return string.Empty; }
    }

    /// <summary>Runs powercfg.exe with the given args (expected to include /output "&lt;path&gt;"),
    /// waits up to timeout, and reports success as "did the output file actually get produced" -
    /// powercfg /energy's own exit code is the number of Errors found, not a 0/nonzero success
    /// flag, so exit code can't be used as the success signal here.</summary>
    private static async Task<(bool Produced, string Output)> RunToFileAsync(string args, string outputPath, TimeSpan timeout, CancellationToken ct)
    {
        try { Directory.CreateDirectory(ReportsDir); }
        catch (Exception ex) { return (false, $"Couldn't create the report folder: {ex.Message}"); }

        try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* best-effort */ }

        try
        {
            var psi = new ProcessStartInfo("powercfg.exe", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "couldn't start powercfg.exe");

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return (false, "powercfg timed out");
            }

            string combined = (await outTask) + (await errTask);
            return (File.Exists(outputPath), combined.Trim());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
