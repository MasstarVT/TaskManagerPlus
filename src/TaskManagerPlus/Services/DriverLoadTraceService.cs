using System.Diagnostics;
using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// #465: best-effort NT Kernel Logger capture (via logman.exe, the same "known Windows tool over
/// raw ETW consumption" tradeoff this app takes everywhere else) of kernel image-load events, to
/// catch drivers/modules loading and unloading at runtime. This codebase has no TraceEvent-style
/// ETL parser (confirmed by the earlier "hard-fault attribution" #433 chunk - see
/// PageFaultTraceService's own remarks, whose exact Start/Stop/logman shape this duplicates), so
/// the payoff here is strictly "here's a real .etl trace, open it in Windows Performance Analyzer" -
/// full in-app ETW event consumption is out of scope without a new heavy dependency.
///
/// IMAGE_LOAD is the NT Kernel Logger keyword for image (driver/DLL) load/unload events; PROC_THREAD
/// is included alongside it the same way PageFaultTraceService pairs its own keywords with
/// PROC_THREAD, so loaded images can at least be correlated with the process that triggered the
/// load when inspecting the trace in WPA.
/// </summary>
public static class DriverLoadTraceService
{
    private const string SessionName = "TaskManagerPlusDriverLoadTrace";
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

        // Best-effort cleanup in case a previous run crashed mid-capture and left the kernel
        // session registered - logman just reports "not running" if there's nothing to stop.
        await RunLogmanAsync($"stop {SessionName} -ets");

        var (success, _, error) = await RunLogmanAsync(
            $"start {SessionName} -p \"Windows Kernel Trace\" (PROC_THREAD,IMAGE_LOAD) -o \"{filePath}\" -ets");

        return success
            ? (true, filePath, "Kernel image-load trace capture started - driver/module loads and unloads are being recorded to the .etl file until you stop the capture.")
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
