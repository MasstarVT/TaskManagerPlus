using System.Diagnostics;
using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// #448: launches the built-in Windows Memory Diagnostic tool (mdsched.exe) - a known Windows tool
/// shelled out to, not reimplemented (the same "prefer a known tool over raw interop" rule this
/// app already follows for schtasks/sc/vssadmin/etc.). mdsched.exe itself shows the "Restart now"
/// vs. "Check for problems the next time I start my computer" dialog and schedules the boot-time
/// test via its own mechanism (a BCD boot entry) - this service only starts that process; the
/// actual 10-40-minute offline test always happens outside this app entirely.
/// </summary>
public static class MemoryDiagnosticLauncherService
{
    /// <summary>Starts mdsched.exe (found via the system directory, same as every other shelled-out
    /// tool in this app) and returns immediately - mdsched shows its own UI and this app never
    /// waits on it. False + an error message on any failure (e.g. missing on a stripped-down
    /// Windows install) rather than throwing out of a command handler.</summary>
    public static (bool Success, string? Error) Launch()
    {
        try
        {
            string exePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mdsched.exe");
            if (!File.Exists(exePath))
            {
                // Fall back to letting the shell resolve it from PATH - covers the unusual case of
                // a non-standard System32 layout without hardcoding a second lookup rule.
                exePath = "mdsched.exe";
            }

            var startInfo = new ProcessStartInfo(exePath) { UseShellExecute = true };
            using var process = Process.Start(startInfo);
            return process is not null
                ? (true, null)
                : (false, "mdsched.exe didn't start.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
