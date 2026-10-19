using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

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
            // Readable by this account and by the signed-in user, whose browser opens it - they
            // differ when an administrator's credentials were typed into the UAC prompt - not by
            // every local account.
            var sub = QuarantineService.CreateOwnerReadableDirectory(Path.Combine(dir.FullName, "report"), SignedInUserSid());
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

    /// <summary>The account signed in to this app's session (the one the desktop shell runs as),
    /// or null when it can't be read - the report is then readable by this account only.</summary>
    private static SecurityIdentifier? SignedInUserSid()
    {
        try
        {
            string? user = QuerySessionString(WtsUserName);
            if (string.IsNullOrEmpty(user)) return null;
            string? domain = QuerySessionString(WtsDomainName);
            var account = string.IsNullOrEmpty(domain) ? new NTAccount(user) : new NTAccount(domain, user);
            return account.Translate(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch
        {
            return null;
        }
    }

    private static string? QuerySessionString(int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession, infoClass, out IntPtr buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    private const int WtsCurrentSession = -1;
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}
