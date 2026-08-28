using System.Diagnostics;
using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// #988: "Print report" - writes a generated HTML report to a temp file and hands it to the OS's
/// own registered print handler via <c>Process.Start(UseShellExecute = true, Verb = "print")</c>
/// (normally the default browser). Deliberately not a full <c>System.Printing</c>/XPS pipeline -
/// that would need building an XPS-compatible document from scratch (WPF's own print APIs don't
/// print arbitrary HTML), a much bigger lift for marginal benefit over reusing the browser's own
/// battle-tested HTML/CSS print rendering, which is exactly what already understands the
/// @media print rules SummaryViewModel.BuildReportCss emits.
/// </summary>
public static class ReportPrintService
{
    public static void PrintHtml(string html, string fileNamePrefix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{fileNamePrefix}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.html");
        File.WriteAllText(path, html);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "print" });
    }
}
