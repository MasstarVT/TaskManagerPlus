using System.Diagnostics;
using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// #465: best-effort ETW capture (via logman.exe, the same "known Windows tool over raw ETW
/// consumption" tradeoff this app takes everywhere else) of image-load events, to catch
/// drivers/modules loading and unloading at runtime. This codebase has no TraceEvent-style
/// ETL parser (confirmed by the earlier "hard-fault attribution" #433 chunk - see
/// PageFaultTraceService's own remarks, whose exact Start/Stop/logman shape this duplicates), so
/// the payoff here is strictly "here's a real .etl trace, open it in Windows Performance Analyzer" -
/// full in-app ETW event consumption is out of scope without a new heavy dependency.
///
/// The provider is the manifest-based Microsoft-Windows-Kernel-Process, not the classic "Windows
/// Kernel Trace": that one only runs under the reserved, system-wide "NT Kernel Logger" session
/// name (any other name is rejected - see DpcLatencyService's remarks), which the Responsiveness
/// tab's DPC/ISR sampling stops and restarts every few seconds. Kernel-Process takes an ordinary
/// session name. Its keywords (`logman query providers Microsoft-Windows-Kernel-Process`):
/// 0x40 image (events 5/6, ImageLoad/ImageUnload), plus 0x10 process and 0x20 thread so loaded
/// images can be correlated with the process that triggered the load when inspecting in WPA.
/// </summary>
public static class DriverLoadTraceService
{
    private const string SessionName = "TaskManagerPlusDriverLoadTrace";
    private const string Provider = "Microsoft-Windows-Kernel-Process";
    private const string Keywords = "0x70";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Starts the trace session, writing to a fresh file under
    /// AppPaths.SettingsDirectory\Traces. Returns the file path on success (not finalized until
    /// StopAsync runs) or a null path plus a plain-English reason on failure.</summary>
    public static async Task<(bool Success, string? FilePath, string Message)> StartAsync()
    {
        string tracesDir = AppPaths.GetPath("Traces");
        try { Directory.CreateDirectory(tracesDir); }
        catch (Exception ex) { return (false, null, $"Couldn't create the traces folder: {ex.Message}"); }

        string filePath = Path.Combine(tracesDir, $"DriverLoads-{DateTime.Now:yyyyMMdd-HHmmss}.etl");
        if (AppPaths.FindRedirectedOutput(filePath) is { } redirected)
            return (false, null, $"Refused to start the trace: {redirected}.");

        // Best-effort cleanup in case a previous run crashed mid-capture and left the kernel
        // session registered - logman just reports "not running" if there's nothing to stop.
        await RunLogmanAsync($"stop {SessionName} -ets");

        var (success, _, error) = await RunLogmanAsync(
            $"create trace {SessionName} -p \"{Provider}\" {Keywords} 5 -o \"{filePath}\" -ets");

        return success
            ? (true, filePath, "Image-load trace capture started - driver/module loads and unloads are being recorded to the .etl file until you stop the capture.")
            : (false, null, string.IsNullOrWhiteSpace(error) ? "logman couldn't start the trace session." : error);
    }

    /// <summary>Stops the session started by StartAsync, finalizing the .etl file.</summary>
    public static async Task<(bool Success, string Message)> StopAsync()
    {
        var (success, _, error) = await RunLogmanAsync($"stop {SessionName} -ets");
        return success
            ? (true, "Trace saved. Open the .etl file in Windows Performance Analyzer (WPA) to inspect individual driver/module load and unload events.")
            : (false, string.IsNullOrWhiteSpace(error) ? "logman couldn't stop the trace session." : error);
    }

    /// <summary>Synchronous best-effort stop for app shutdown (DevicesDriversViewModel.Dispose on the
    /// UI thread) - the async path would deadlock there, since RunLogmanAsync resumes on the
    /// captured context. Bounded so a wedged logman can't hold up exit.</summary>
    public static void StopOnExit()
    {
        try { ToolRunner.RunCaptured("logman.exe", $"stop {SessionName} -ets", TimeSpan.FromSeconds(5)); }
        catch { /* best-effort - nothing more to do at exit */ }
    }

    private static async Task<(bool Success, string Output, string Error)> RunLogmanAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("logman.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, string.Empty, "Couldn't start logman.exe.");

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(CommandTimeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return (false, string.Empty, "logman.exe timed out.");
            }

            string output = await outputTask;
            string error = await errorTask;
            return (proc.ExitCode == 0, output, string.IsNullOrWhiteSpace(error) ? output : error);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
}
