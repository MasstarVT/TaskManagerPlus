using System.Diagnostics;
using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// #433 (bonus, best-effort half): shells out to logman.exe to capture a short NT Kernel Trace
/// (pf/hf keywords) to an .etl file, the same "known Windows tool over raw ETW
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
///
/// Page faults exist only on the classic "Windows Kernel Trace" provider (the manifest-based
/// Microsoft-Windows-Kernel-Memory has no page-fault keyword), and that provider only runs under
/// the one reserved, system-wide "NT Kernel Logger" session name - see DpcLatencyService's remarks.
/// The Responsiveness tab's DPC/ISR sampling uses the same session, so <see cref="IsCapturing"/>
/// tells it to stand off while this capture holds it, and this never stops the session blindly.
/// </summary>
public static class PageFaultTraceService
{
    private const string SessionName = "NT Kernel Logger";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>True from just before StartAsync's logman start until the capture ends - set before
    /// the start so DpcLatencyService's per-sample stop can't land in between and end it.</summary>
    public static bool IsCapturing { get; private set; }

    // Present while this app's capture holds the kernel logger, so a session a crash or kill left
    // behind can be told apart from one another tool owns.
    private static string MarkerPath => AppPaths.GetPath("pagefault-trace-armed.marker");

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

        if (IsCapturing) return (false, null, "A page-fault trace capture is already running.");
        IsCapturing = true;

        // Only a session this app's own crashed run left behind is stopped first - the name is
        // shared system-wide, so a blind stop would end whoever else owns it.
        if (File.Exists(MarkerPath)) await RunLogmanAsync($"stop \"{SessionName}\" -ets");

        // Keyword names are logman's own (`logman query providers "Windows Kernel Trace"`), not
        // xperf's flag names. -max (MB) bounds the file if the session outlives this app.
        var (success, _, error) = await RunLogmanAsync(
            $"start \"{SessionName}\" -p \"Windows Kernel Trace\" (process,thread,pf,hf) -o \"{filePath}\" -max 512 -ets");

        if (!success)
        {
            IsCapturing = false;
            ClearMarker();
            string message = error.Contains("already", StringComparison.OrdinalIgnoreCase)
                ? "The NT Kernel Logger is already in use by another trace (the Responsiveness tab's DPC/ISR measurement, WPR, xperf, ...) - only one can run at a time."
                : string.IsNullOrWhiteSpace(error) ? "logman couldn't start the trace session." : error;
            return (false, null, message);
        }

        try { File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o")); } catch { /* best-effort */ }
        return (true, filePath, "Kernel trace capture started.");
    }

    /// <summary>Stops the session started by StartAsync, finalizing the .etl file.</summary>
    public static async Task<(bool Success, string Message)> StopAsync()
    {
        var (success, _, error) = await RunLogmanAsync($"stop \"{SessionName}\" -ets");
        IsCapturing = false;
        ClearMarker();
        return success
            ? (true, "Trace saved.")
            : (false, string.IsNullOrWhiteSpace(error) ? "logman couldn't stop the trace session." : error);
    }

    /// <summary>Synchronous, bounded stop for app shutdown (the async path is a dispatcher
    /// continuation that never runs once the dispatcher has shut down). A no-op unless this app's
    /// capture is the one holding the kernel logger.</summary>
    public static void StopOnExit()
    {
        if (!IsCapturing) return;
        try { ToolRunner.RunCaptured("logman.exe", $"stop \"{SessionName}\" -ets", TimeSpan.FromSeconds(5)); }
        catch { /* best-effort - nothing more to do at exit */ }
        IsCapturing = false;
        ClearMarker();
    }

    private static void ClearMarker()
    {
        try { File.Delete(MarkerPath); } catch { /* best-effort */ }
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
