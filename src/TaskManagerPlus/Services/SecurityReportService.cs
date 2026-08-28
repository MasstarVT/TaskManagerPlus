using System.Text;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 13, #805: security posture report export - the same "self-contained Markdown/HTML text
/// built by hand with a StringBuilder, CSV escaped the same way LoggingService escapes its rows"
/// shape SummaryViewModel's GenerateReport/GenerateHtmlReport already use for the diagnostic
/// report, extended with a redaction pass so the file can be safely posted to a help forum.
///
/// This chunk's report covers Findings and flagged autoruns; "Protection status" is left as an
/// explicit placeholder section - later chunks (Defender status, mitigation policies, firewall
/// profile, ...) populate it with real data rather than this chunk guessing at a shape up front.
/// </summary>
public static class SecurityReportService
{
    public static string BuildMarkdownReport(IEnumerable<AutorunEntry> autoruns, IEnumerable<SecurityFinding> findings, bool redact)
    {
        var redactor = Redactor.Create(redact);
        var findingList = findings.OrderByDescending(f => f.Severity).ToList();
        var autorunList = autoruns.ToList();

        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("# Task Manager Plus security posture report");
        Line($"Generated {DateTime.Now:F}");
        if (redact) Line("_Usernames, machine name, and serial-like strings have been redacted._");
        Line();
        Line("These are quick flags for a human to judge, not a verdict that something is malicious.");
        Line();

        Line("## Protection status");
        Line("_Not yet populated - a later round adds Defender/firewall/mitigation-policy status here._");
        Line();

        Line("## Findings");
        if (findingList.Count == 0)
        {
            Line("No findings from this scan.");
        }
        else
        {
            Line("| Severity | Title | Reason | Path |");
            Line("|---|---|---|---|");
            foreach (var f in findingList)
                Line($"| {f.Severity} | {redactor(f.Title)} | {redactor(f.Reason)} | {redactor(f.Path)} |");
        }
        Line();

        Line("## Flagged autoruns (persistence)");
        if (autorunList.Count == 0)
        {
            Line("No persistence scan has been run yet.");
        }
        else
        {
            Line("| Category | Name | Path | Signature | Location | Enabled |");
            Line("|---|---|---|---|---|---|");
            foreach (var a in autorunList)
                Line($"| {redactor(a.Category)} | {redactor(a.Name)} | {redactor(a.ResolvedPath)} | {a.SignatureStatus} | {redactor(a.Location)} | {a.Enabled} |");
        }

        return sb.ToString();
    }

    public static string BuildHtmlReport(IEnumerable<AutorunEntry> autoruns, IEnumerable<SecurityFinding> findings, bool redact)
    {
        var redactor = Redactor.Create(redact);
        static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);
        var findingList = findings.OrderByDescending(f => f.Severity).ToList();
        var autorunList = autoruns.ToList();

        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("<!doctype html><html><head><meta charset=\"utf-8\">");
        Line($"<title>Task Manager Plus security report - {Esc(DateTime.Now.ToString("F"))}</title>");
        Line("<style>" +
             "body{font-family:Segoe UI,Arial,sans-serif;background:#1c1c1f;color:#e4e4e7;max-width:960px;margin:32px auto;padding:0 16px}" +
             "h1{font-size:20px}h2{font-size:15px;border-bottom:1px solid #3a3a42;padding-bottom:6px;margin-top:28px}" +
             "table{border-collapse:collapse;width:100%;font-size:13px}td,th{padding:4px 8px;text-align:left;border-bottom:1px solid #2c2c33}" +
             ".sev-high{color:#f26d6d}.sev-medium{color:#e8b23c}.sev-low,.sev-info{color:#9a9aa2}.muted{color:#9a9aa2;font-size:12px}" +
             "</style></head><body>");

        Line($"<h1>Task Manager Plus security posture report</h1><p class=\"muted\">Generated {Esc(DateTime.Now.ToString("F"))}");
        if (redact) Line("<br>Usernames, machine name, and serial-like strings have been redacted.");
        Line("</p>");
        Line("<p>These are quick flags for a human to judge, not a verdict that something is malicious.</p>");

        Line("<h2>Protection status</h2><p class=\"muted\">Not yet populated - a later round adds Defender/firewall/mitigation-policy status here.</p>");

        Line("<h2>Findings</h2>");
        if (findingList.Count == 0)
        {
            Line("<p>No findings from this scan.</p>");
        }
        else
        {
            Line("<table><tr><th>Severity</th><th>Title</th><th>Reason</th><th>Path</th></tr>");
            foreach (var f in findingList)
                Line($"<tr class=\"sev-{f.Severity.ToString().ToLowerInvariant()}\"><td>{f.Severity}</td><td>{Esc(redactor(f.Title))}</td><td>{Esc(redactor(f.Reason))}</td><td>{Esc(redactor(f.Path))}</td></tr>");
            Line("</table>");
        }

        Line("<h2>Flagged autoruns (persistence)</h2>");
        if (autorunList.Count == 0)
        {
            Line("<p>No persistence scan has been run yet.</p>");
        }
        else
        {
            Line("<table><tr><th>Category</th><th>Name</th><th>Path</th><th>Signature</th><th>Location</th><th>Enabled</th></tr>");
            foreach (var a in autorunList)
                Line($"<tr><td>{Esc(redactor(a.Category))}</td><td>{Esc(redactor(a.Name))}</td><td>{Esc(redactor(a.ResolvedPath))}</td><td>{Esc(a.SignatureStatus)}</td><td>{Esc(redactor(a.Location))}</td><td>{a.Enabled}</td></tr>");
            Line("</table>");
        }

        Line("</body></html>");
        return sb.ToString();
    }

    public static string BuildCsvReport(IEnumerable<AutorunEntry> autoruns, IEnumerable<SecurityFinding> findings, bool redact)
    {
        var redactor = Redactor.Create(redact);

        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append('\n');

        Line("Section,Severity,Category,Name,Path,Signature,Location,Enabled,Reason");

        foreach (var f in findings.OrderByDescending(f => f.Severity))
        {
            var fields = new[]
            {
                "Finding", f.Severity.ToString(), string.Empty, redactor(f.Title), redactor(f.Path),
                string.Empty, string.Empty, string.Empty, redactor(f.Reason),
            };
            Line(string.Join(",", fields.Select(Escape)));
        }

        foreach (var a in autoruns)
        {
            var fields = new[]
            {
                "Autorun", string.Empty, redactor(a.Category), redactor(a.Name), redactor(a.ResolvedPath),
                a.SignatureStatus, redactor(a.Location), a.Enabled.ToString(), string.Empty,
            };
            Line(string.Join(",", fields.Select(Escape)));
        }

        return sb.ToString();
    }

    // Same escaping rule as LoggingService.Escape - only quote a field when it actually needs it.
    private static string Escape(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Blanks usernames, the machine name, and serial-like strings (product-key-shaped
    /// tokens, long digit runs) before a value goes into the report - on by default from the UI so
    /// a report can be safely posted to a help forum. This is a coarse heuristic, not a guarantee
    /// every identifying string is caught; later chunks can extend the pattern list.</summary>
    private static class Redactor
    {
        private static readonly Regex ProductKeyLike = new(@"\b([A-Za-z0-9]{5}-){4}[A-Za-z0-9]{5}\b", RegexOptions.Compiled);
        private static readonly Regex LongDigitRun = new(@"\b\d{10,}\b", RegexOptions.Compiled);

        public static Func<string, string> Create(bool enabled)
        {
            if (!enabled) return s => s;

            var userName = Environment.UserName;
            var machineName = Environment.MachineName;
            var userNamePattern = string.IsNullOrEmpty(userName) ? null : new Regex(Regex.Escape(userName), RegexOptions.IgnoreCase);
            var machineNamePattern = string.IsNullOrEmpty(machineName) ? null : new Regex(Regex.Escape(machineName), RegexOptions.IgnoreCase);

            return text =>
            {
                if (string.IsNullOrEmpty(text)) return text;
                var result = text;
                if (userNamePattern is not null) result = userNamePattern.Replace(result, "[user]");
                if (machineNamePattern is not null) result = machineNamePattern.Replace(result, "[machine]");
                result = ProductKeyLike.Replace(result, "[serial]");
                result = LongDigitRun.Replace(result, "[serial]");
                return result;
            };
        }
    }
}
