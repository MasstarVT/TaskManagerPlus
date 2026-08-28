using System.Diagnostics;
using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// #210: offline DPC/ISR capture for a user who'd rather not run a live sampling session - shells
/// out to the in-box wpr.exe (Windows Performance Recorder) with its built-in "GeneralProfile"
/// (covers CPU usage, DPC/ISR, and disk - a superset of what this tab needs, but there's no
/// narrower in-box built-in profile), then converts the resulting .etl to a browsable HTML report
/// via tracerpt -report. Both tools ship in System32 on Windows 10/11 - unlike xperf (part of the
/// separate Windows Performance Toolkit / ADK, not in-box), so IsAvailable being false should be
/// rare, but is still checked rather than assumed.
///
/// Deliberately doesn't try to parse the report itself the way DpcLatencyService parses its own
/// short samples - the whole point of this path is "hand the user a real trace to open in WPA",
/// not a second data-parsing pipeline to maintain.
/// </summary>
public static class WprCaptureService
{
    private static readonly string WprPath = Path.Combine(Environment.SystemDirectory, "wpr.exe");
    private static readonly string TracerptPath = Path.Combine(Environment.SystemDirectory, "tracerpt.exe");

    public static bool IsAvailable => File.Exists(WprPath) && File.Exists(TracerptPath);

    public static async Task<(bool Ok, string Message, string? EtlPath, string? ReportPath)> CaptureAsync(TimeSpan duration, CancellationToken ct)
    {
        if (!IsAvailable)
            return (false, "wpr.exe/tracerpt.exe weren't found on this system - offline capture isn't available.", null, null);

        string dir = Path.Combine(AppPaths.SettingsDirectory, "WprCaptures");
        try { Directory.CreateDirectory(dir); }
        catch { return (false, "Couldn't create a folder for the capture.", null, null); }

        string etl = Path.Combine(dir, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.etl");
        string report = Path.ChangeExtension(etl, ".report.html");

        // Best-effort: cancel any stale WPR session left over from a previous crashed/killed run.
        await RunAsync(WprPath, "-cancel", TimeSpan.FromSeconds(10), CancellationToken.None, ignoreExitCode: true);

        var (startOk, startMsg) = await RunAsync(WprPath, "-start GeneralProfile -filemode", TimeSpan.FromSeconds(30), ct);
        if (!startOk)
            return (false, $"Couldn't start the WPR capture: {startMsg}", null, null);

        try
        {
            await Task.Delay(duration, ct);
        }
        catch (OperationCanceledException)
        {
            await RunAsync(WprPath, "-cancel", TimeSpan.FromSeconds(10), CancellationToken.None, ignoreExitCode: true);
            throw;
        }

        var (stopOk, stopMsg) = await RunAsync(WprPath, $"-stop \"{etl}\"", TimeSpan.FromSeconds(60), CancellationToken.None);
        if (!stopOk || !File.Exists(etl))
            return (false, $"Couldn't stop/save the WPR capture: {stopMsg}", null, null);

        var (reportOk, reportMsg) = await RunAsync(TracerptPath, $"\"{etl}\" -report \"{report}\" -f html -y", TimeSpan.FromSeconds(120), CancellationToken.None);
        return reportOk && File.Exists(report)
            ? (true, "Capture complete.", etl, report)
            : (true, $"Capture saved, but the tracerpt report step failed: {reportMsg}", etl, null);
    }

    /// <summary>Opens the captured .etl (in WPA if installed, otherwise whatever's associated) or
    /// the HTML report, the same shell-out-to-open pattern OpenUpdateUrlCommand already uses.</summary>
    public static void OpenInDefaultApp(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* best-effort - nothing else to do if there's no shell association */ }
    }

    private static async Task<(bool Ok, string Output)> RunAsync(string exe, string args, TimeSpan timeout, CancellationToken ct, bool ignoreExitCode = false)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "couldn't start process");

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await proc.WaitForExitAsync(cts.Token);

            string combined = (await outTask) + (await errTask);
            bool ok = ignoreExitCode || proc.ExitCode == 0;
            return (ok, combined.Trim());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "timed out");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
