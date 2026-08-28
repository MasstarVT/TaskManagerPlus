namespace TaskManagerPlus.Services;

/// <summary>
/// #700: the handful of genuinely reusable, pure pieces of #73/#97's diagnostic-report HTML
/// (SummaryViewModel.BuildReportHtml/Sparkline) - extracted here so StressTestReportService can
/// build its own run reports through the same "existing CSV/HTML/Markdown reporting system"
/// (CLAUDE.md) rather than hand-rolling a second, unrelated HTML writer. SummaryViewModel's own
/// BuildReportHtml/BuildReportMarkdown stay in place (they're tightly coupled to that ViewModel's
/// own fields - specs, health issues, top processes - and extracting THOSE out would just move the
/// coupling, not remove it) but now call through to HtmlEscape/Sparkline here instead of keeping
/// private duplicates, so both reports render with the identical look.
/// </summary>
public static class DiagnosticReportFormatting
{
    public static string HtmlEscape(string s) => System.Net.WebUtility.HtmlEncode(s);

    /// <summary>The shared "self-contained HTML file" opening boilerplate (charset, title, inline
    /// &lt;style&gt;, opening &lt;body&gt;) both SummaryViewModel's and StressTestReportService's
    /// reports use - same dark, no-external-reference styling either way.</summary>
    public static string HtmlDocumentOpen(string titleText)
    {
        return "<!doctype html><html><head><meta charset=\"utf-8\">" +
               $"<title>{HtmlEscape(titleText)}</title>" +
               "<style>" +
               "body{font-family:Segoe UI,Arial,sans-serif;background:#1c1c1f;color:#e4e4e7;max-width:900px;margin:32px auto;padding:0 16px}" +
               "h1{font-size:20px}h2{font-size:15px;border-bottom:1px solid #3a3a42;padding-bottom:6px;margin-top:28px}" +
               "table{border-collapse:collapse;width:100%;font-size:13px}td,th{padding:4px 8px;text-align:left;border-bottom:1px solid #2c2c33}" +
               ".crit{color:#f26d6d}.warn{color:#e8b23c}.ok{color:#4fd18b}.muted{color:#9a9aa2;font-size:12px}" +
               "svg{background:#242429;border-radius:6px}</style></head><body>";
    }

    /// <summary>Renders one history buffer as a small inline SVG polyline - no chart library, just
    /// a hand-built path, so the file stays a single self-contained .html with no external script/
    /// CSS reference. <paramref name="min"/>/<paramref name="max"/> default to 0-100 (a percent
    /// series, SummaryViewModel's own original use) - StressTestReportService passes the trace's
    /// actual min/max instead, since a temperature or clock-speed series isn't 0-100 bounded.</summary>
    public static string Sparkline(IEnumerable<double> values, string color, double min = 0, double max = 100)
    {
        var list = values.ToList();
        if (list.Count < 2) return string.Empty;
        if (max <= min) max = min + 1; // degenerate range (every sample identical) - avoid divide-by-zero

        const int width = 600, height = 60;
        var points = list.Select((v, i) =>
        {
            double x = i / (double)(list.Count - 1) * width;
            double y = height - Math.Clamp((v - min) / (max - min), 0, 1) * height;
            return $"{x:0.#},{y:0.#}";
        });
        return $"<svg viewBox=\"0 0 {width} {height}\" width=\"100%\" height=\"{height}\">" +
               $"<polyline points=\"{string.Join(' ', points)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" /></svg>";
    }
}
