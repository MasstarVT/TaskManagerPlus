using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// #988: "Print report" - writes a generated HTML report to a file and opens it in the user's
/// default browser, which brings up its own print dialog (a small onload window.print() call).
/// Deliberately not a full <c>System.Printing</c>/XPS pipeline - that would need building an
/// XPS-compatible document from scratch (WPF's own print APIs don't print arbitrary HTML), a much
/// bigger lift for marginal benefit over reusing the browser's own battle-tested HTML/CSS print
/// rendering, which is exactly what already understands the @media print rules
/// SummaryViewModel.BuildReportCss emits.
///
/// The file goes in an AdminDataPaths private folder (not %TEMP%, where any unelevated process
/// running as the user could rewrite it before it's opened) and is opened unelevated through
/// SafeLaunchService - an elevated "print" verb would run whatever handler the user's HKCU
/// associations name, as Administrator. Leftover folders are swept by AdminDataPaths.PurgeStaleTemp.
/// </summary>
public static class ReportPrintService
{
    /// <summary>Throws with a short reason when the report couldn't be written or opened.</summary>
    public static void PrintHtml(string html, string fileNamePrefix)
    {
        var dir = AdminDataPaths.CreatePrivateTempDirectory("Print", out string? error)
            ?? throw new InvalidOperationException($"couldn't create a protected folder for the report ({error})");
        string safePrefix = string.Concat(fileNamePrefix.Where(c => char.IsLetterOrDigit(c) || c == '-'));
        try
        {
            // Readable by this user (the browser opening it runs as them), not every local account.
            var sub = QuarantineService.CreateOwnerReadableDirectory(Path.Combine(dir.FullName, "report"));
            string path = Path.Combine(sub.FullName, $"{(safePrefix.Length > 0 ? safePrefix : "report")}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.html");
            File.WriteAllText(path, WithPrintOnLoad(html));
            if (!SafeLaunchService.OpenDocument(path))
                throw new InvalidOperationException("couldn't open the report in your default browser");
        }
        catch
        {
            AdminDataPaths.TryDelete(dir);
            throw;
        }
    }

    private const string PrintScript = "<script>window.addEventListener('load', function () { window.print(); });</script>";

    private static string WithPrintOnLoad(string html)
    {
        int body = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return body >= 0 ? html.Insert(body, PrintScript) : html + PrintScript;
    }
}
