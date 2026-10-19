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

    /// <summary>#298: true while a circular (buffer-mode) session started by
    /// <see cref="StartCircularAsync"/> is running - IncidentBundleService/ResponsivenessViewModel
    /// use this to know whether a trigger has anything to stop-and-save.</summary>
    public static bool IsCircularCaptureRunning { get; private set; }

    /// <summary>
    /// #298: starts an in-memory circular ETW capture - `wpr -start GeneralProfile` with no
    /// `-filemode` flag defaults to an in-memory ring buffer rather than streaming straight to
    /// disk (unlike #210's file-mode <see cref="CaptureAsync"/> above), so it can be left running
    /// continuously at low cost and only actually written to disk when something interesting just
    /// happened (#297's trigger, via <see cref="StopCircularAsync"/>). Off by default, always an
    /// explicit opt-in toggle - see the item's own "state the overhead plainly" instruction.
    ///
    /// Fails with the same honest "another session owns the logger" wording
    /// DpcLatencyService/HardFaultEtwService/PresentMonitorService already use for the analogous
    /// logman failure, since it's the identical underlying condition (only one ETW session per
    /// logger is allowed) just surfaced through a different tool.
    /// </summary>
    public static async Task<(bool Ok, string Message)> StartCircularAsync(CancellationToken ct)
    {
        if (!IsAvailable)
            return (false, "wpr.exe/tracerpt.exe weren't found on this system - ETW circular capture isn't available.");
        if (IsCircularCaptureRunning)
            return (true, "Circular capture already running.");

        // A WPR session outlives the process that started it (until reboot), so one this app armed
        // and never tore down - a crash or kill - makes every later -start fail. The marker file
        // says exactly that happened, so only then is it cancelled first; an unrelated WPR session
        // (another tab's capture, the user's own) is left alone. The marker is shared with
        // CaptureAsync's file-mode session, so a file-mode capture in flight right now isn't one.
        if (File.Exists(ArmedMarkerPath) && !IsFileCaptureRunning)
            await RunAsync(WprPath, "-cancel", TimeSpan.FromSeconds(10), CancellationToken.None, ignoreExitCode: true);

        var (startOk, startMsg) = await RunAsync(WprPath, "-start GeneralProfile", TimeSpan.FromSeconds(30), ct);
        if (!startOk)
        {
            ClearArmedMarker();
            string message = startMsg.Contains("already running", StringComparison.OrdinalIgnoreCase) || startMsg.Contains("already collecting", StringComparison.OrdinalIgnoreCase)
                ? "A trace session was already running and couldn't be replaced."
                : $"Couldn't start the ETW circular capture: {startMsg}";
            return (false, message);
        }

        IsCircularCaptureRunning = true;
        try { File.WriteAllText(ArmedMarkerPath, DateTime.UtcNow.ToString("o")); } catch { /* best-effort */ }
        return (true, "ETW circular capture running (in-memory buffer, nothing written to disk yet).");
    }

    // Present while a WPR session this app started (circular or CaptureAsync's file-mode one) may
    // still be running; the file name predates the file-mode use.
    private static string ArmedMarkerPath => AppPaths.GetPath("wpr-circular-armed.marker");

    private static bool IsFileCaptureRunning { get; set; }

    private static void ClearArmedMarker()
    {
        try { File.Delete(ArmedMarkerPath); } catch { /* best-effort */ }
    }

    /// <summary>#298: stops the circular session and saves whatever the in-memory buffer held
    /// (typically the last ~60s, matching #296's own ring-buffer window) to <paramref name="etlPath"/>
    /// - called from #297's trigger handler so the trace covering the moment of the stutter is
    /// preserved before the ring buffer wraps past it.</summary>
    public static async Task<(bool Ok, string Message, string? EtlPath)> StopCircularAsync(string etlPath)
    {
        if (!IsCircularCaptureRunning)
            return (false, "No circular capture is currently running.", null);

        try { Directory.CreateDirectory(Path.GetDirectoryName(etlPath)!); } catch { /* best-effort */ }

        var (stopOk, stopMsg) = await RunAsync(WprPath, $"-stop \"{etlPath}\"", TimeSpan.FromSeconds(60), CancellationToken.None);
        IsCircularCaptureRunning = false;
        ClearArmedMarker();
        return stopOk && File.Exists(etlPath)
            ? (true, "Circular capture saved.", etlPath)
            : (false, $"Couldn't stop/save the circular capture: {stopMsg}", null);
    }

    /// <summary>Best-effort cancel of a circular session without saving it - used when the user
    /// turns the #298 toggle back off without a trigger having fired.</summary>
    public static async Task CancelCircularAsync()
    {
        if (!IsCircularCaptureRunning) return;
        await RunAsync(WprPath, "-cancel", TimeSpan.FromSeconds(10), CancellationToken.None, ignoreExitCode: true);
        IsCircularCaptureRunning = false;
        ClearArmedMarker();
    }

    /// <summary>Synchronous best-effort `wpr -cancel`, for app shutdown: a WPR session (circular or a
    /// file-mode capture still in flight) keeps running in the kernel after this process exits,
    /// and the async cancel paths are dispatcher continuations that never run once the dispatcher
    /// has shut down.</summary>
    public static void CancelBlocking()
    {
        if (!IsAvailable) return;
        try { ToolRunner.RunCaptured(WprPath, "-cancel", TimeSpan.FromSeconds(5)); }
        catch { /* best-effort */ }
        IsCircularCaptureRunning = false;
        IsFileCaptureRunning = false;
        ClearArmedMarker();
    }

    public static async Task<(bool Ok, string Message, string? EtlPath, string? ReportPath)> CaptureAsync(TimeSpan duration, CancellationToken ct)
    {
        if (!IsAvailable)
            return (false, "wpr.exe/tracerpt.exe weren't found on this system - offline capture isn't available.", null, null);

        string dir = Path.Combine(AppPaths.SettingsDirectory, "WprCaptures");
        try { Directory.CreateDirectory(dir); }
        catch { return (false, "Couldn't create a folder for the capture.", null, null); }

        string etl = Path.Combine(dir, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.etl");
        string report = Path.ChangeExtension(etl, ".report.html");

        // Cancel first only a stale session this app's own crashed/killed run left behind (the
        // marker) - a blind `wpr -cancel` would also end the ETW panel's capture or the user's own
        // WPR recording. Any other session already running makes -start below fail instead.
        if (!IsCircularCaptureRunning && File.Exists(ArmedMarkerPath))
        {
            await RunAsync(WprPath, "-cancel", TimeSpan.FromSeconds(10), CancellationToken.None, ignoreExitCode: true);
            ClearArmedMarker();
        }

        var (startOk, startMsg) = await RunAsync(WprPath, "-start GeneralProfile -filemode", TimeSpan.FromSeconds(30), ct);
        if (!startOk)
            return (false, $"Couldn't start the WPR capture: {startMsg}", null, null);

        IsFileCaptureRunning = true;
        try { File.WriteAllText(ArmedMarkerPath, DateTime.UtcNow.ToString("o")); } catch { /* best-effort */ }

        try
        {
            await Task.Delay(duration, ct);
        }
        catch (OperationCanceledException)
        {
            await RunAsync(WprPath, "-cancel", TimeSpan.FromSeconds(10), CancellationToken.None, ignoreExitCode: true);
            IsFileCaptureRunning = false;
            ClearArmedMarker();
            throw;
        }

        var (stopOk, stopMsg) = await RunAsync(WprPath, $"-stop \"{etl}\"", TimeSpan.FromSeconds(60), CancellationToken.None);
        IsFileCaptureRunning = false;
        ClearArmedMarker();
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

    /// <summary>#1084: delegates to the shared <see cref="ToolRunner"/> - notably, the local copy
    /// this replaces never killed a timed-out child at all; the shared runner kills the whole
    /// process tree. Unlike the DpcLatency/HardFault/PresentMonitor siblings this one reports
    /// external cancellation as (false, message) rather than rethrowing - preserved.</summary>
    private static async Task<(bool Ok, string Output)> RunAsync(string exe, string args, TimeSpan timeout, CancellationToken ct, bool ignoreExitCode = false)
    {
        try
        {
            var (output, exitCode) = await ToolRunner.RunCapturedAsync(exe, args, (int)timeout.TotalMilliseconds, ct);
            if (exitCode is null)
            {
                ct.ThrowIfCancellationRequested(); // caught below -> (false, "The operation was canceled.")
                return (false, "timed out");
            }
            return (ignoreExitCode || exitCode == 0, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
