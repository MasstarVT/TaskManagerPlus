using System.Diagnostics;
using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// #433 (bonus, best-effort half): shells out to logman.exe to capture a short NT Kernel Trace
/// (PAGE_FAULTS/HARD_FAULTS keywords) to an .etl file, the same "known Windows tool over raw ETW
/// consumption" tradeoff this app takes everywhere else (schtasks.exe, sc.exe, vssadmin.exe, ...) -
/// this codebase has no TraceEvent-style ETL parser, and adding one from scratch for a single
/// diagnostic button is out of scope, so the payoff here is strictly "here's a real trace file,
/// open it in Windows Performance Analyzer" rather than an in-app event view. The in-app value for
/// this feature comes from MemoryViewModel's separate perf-counter-sampling pass
/// (ScanPageFaultsAsync) that runs alongside this capture, not from this file's contents.
///
/// Every logman invocation is wrapped the same defensive way DiskFragmentationService.Analyze
/// shells out to defrag.exe: redirected output, a bounded wait, and a Kill() on timeout - logman
/// itself returns almost immediately for both start and stop, but a hung/blocked child process
/// should never be able to wedge this on-demand feature.
/// </summary>
public static class PageFaultTraceService
{
    private const string SessionName = "TaskManagerPlusPageFaultTrace";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Starts the trace session, writing to a fresh file under
    /// AppPaths.SettingsDirectory\Traces. Returns the file path on success (the file itself won't
    /// exist/be finalized until StopAsync runs) or a null path plus a plain-English reason on
    /// failure - logman can fail for reasons worth showing verbatim (session already running,
    /// insufficient privilege despite running elevated, kernel trace already owned by another
    /// tool) rather than a generic "failed".</summary>
    public static async Task<(bool Success, string? FilePath, string Message)> StartAsync()
    {
        string tracesDir = AppPaths.GetPath("Traces");
        try { Directory.CreateDirectory(tracesDir); }
        catch (Exception ex) { return (false, null, $"Couldn't create the traces folder: {ex.Message}"); }

        string filePath = Path.Combine(tracesDir, $"PageFaults-{DateTime.Now:yyyyMMdd-HHmmss}.etl");

        // Best-effort cleanup in case a previous run crashed mid-capture and left the kernel
        // session registered - logman just reports "not running" if there's nothing to stop.
        await RunLogmanAsync($"stop {SessionName} -ets");

        var (success, _, error) = await RunLogmanAsync(
            $"start {SessionName} -p \"Windows Kernel Trace\" (PROC_THREAD,PAGE_FAULTS,HARD_FAULTS) -o \"{filePath}\" -ets");

        return success
            ? (true, filePath, "Kernel trace capture started.")
            : (false, null, string.IsNullOrWhiteSpace(error) ? "logman couldn't start the trace session." : error);
    }

    /// <summary>Stops the session started by StartAsync, finalizing the .etl file.</summary>
    public static async Task<(bool Success, string Message)> StopAsync()
    {
        var (success, _, error) = await RunLogmanAsync($"stop {SessionName} -ets");
        return success
            ? (true, "Trace saved.")
            : (false, string.IsNullOrWhiteSpace(error) ? "logman couldn't stop the trace session." : error);
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
                try { proc.Kill(); } catch { /* best-effort */ }
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
