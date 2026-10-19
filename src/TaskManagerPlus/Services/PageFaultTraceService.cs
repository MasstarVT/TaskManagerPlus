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
/// The Responsiveness tab's DPC/ISR sampling uses the same session; both go through
/// <see cref="KernelLoggerSession"/>, so neither can stop the other's session (or another tool's).
/// </summary>
public static class PageFaultTraceService
{
    private const string SessionName = KernelLoggerSession.Name;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>True while this capture holds the kernel logger (claimed before StartAsync's
    /// logman start, released once StopAsync has stopped the session).</summary>
    public static bool IsCapturing => KernelLoggerSession.CurrentOwner == KernelLoggerSession.Owner.PageFaultCapture;

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

        if (!KernelLoggerSession.TryClaim(KernelLoggerSession.Owner.PageFaultCapture, out var holder))
        {
            return (false, null, holder == KernelLoggerSession.Owner.PageFaultCapture
                ? "A page-fault trace capture is already running."
                : "The NT Kernel Logger is in use by the Responsiveness tab's DPC/ISR measurement - stop that measurement first (only one kernel trace can run at a time).");
        }

        // Only a session this app's own crashed run left behind is stopped first - see
        // KernelLoggerSession for how that is told apart from another tool's session.
        await KernelLoggerSession.ReclaimLeakedSessionAsync();

        // Keyword names are logman's own (`logman query providers "Windows Kernel Trace"`), not
        // xperf's flag names. `create trace ... -ets` (DpcLatencyService's form) starts it at once
        // and documents -p/-o/-max; -max (MB) bounds the file if the session outlives this app.
        KernelLoggerSession.RecordStarting(filePath);
        var (success, _, error) = await RunLogmanAsync(
            $"create trace \"{SessionName}\" -p \"Windows Kernel Trace\" \"(process,thread,pf,hf)\" -o \"{filePath}\" -max 512 -ets");

        if (!success)
        {
            // The marker is left for the next claim to validate: a timed-out create may still have
            // started the session, and the marker only ever matches this file.
            KernelLoggerSession.Release(KernelLoggerSession.Owner.PageFaultCapture);
            string message = KernelLoggerSession.LooksLikeAlreadyRunning(error)
                ? "The NT Kernel Logger is already in use by another trace (WPR, xperf, PerfView, ...) - only one can run at a time, and this app doesn't stop a trace it didn't start."
                : string.IsNullOrWhiteSpace(error) ? "logman couldn't start the trace session." : error;
            return (false, null, message);
        }

        return (true, filePath, "Kernel trace capture started.");
    }

    /// <summary>Stops the session started by StartAsync, finalizing the .etl file. Refuses when
    /// this capture doesn't hold the kernel logger, so it can never stop someone else's session.</summary>
    public static async Task<(bool Success, string Message)> StopAsync()
    {
        if (!IsCapturing) return (false, "No page-fault trace capture is running.");

        var (success, _, error) = await RunLogmanAsync($"stop \"{SessionName}\" -ets");
        // A failed stop keeps the marker, so the next claim can still reclaim this session.
        if (success) KernelLoggerSession.RecordStopped();
        KernelLoggerSession.Release(KernelLoggerSession.Owner.PageFaultCapture);
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
        KernelLoggerSession.StopBlocking(KernelLoggerSession.Owner.PageFaultCapture);
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

/// <summary>
/// The one in-app owner of the system-wide "NT Kernel Logger" session. The classic "Windows Kernel
/// Trace" provider runs only under that reserved name (see DpcLatencyService's remarks), so the
/// Responsiveness tab's DPC/ISR sampling and the Memory tab's page-fault capture both need it - and
/// so can xperf, PerfView, WPR or a hand-run logman. Two rules:
///
/// 1. A feature claims the logger before any logman call that touches it and releases the claim
///    only after its own session is stopped. The claim is taken synchronously under a lock, so two
///    features interleaving at an await can never both pass the check.
/// 2. This app never stops a kernel session it can't show it started. A marker file holds the .etl
///    path of the session this app is starting/running; a marker a crash or kill left behind leads
///    to a stop only when `logman query "NT Kernel Logger" -ets` shows the running session writing
///    that same file. The file name is GUID/timestamp-unique and matched as a plain substring, so no
///    (possibly localized) label is parsed. Anything else - not running ("Data Collector Set was
///    not found."), a different output file - just drops the marker; a query that couldn't run or
///    timed out keeps it for the next claim.
/// </summary>
internal static class KernelLoggerSession
{
    public const string Name = "NT Kernel Logger";

    public enum Owner { None, DpcSampling, PageFaultCapture }

    private static readonly string LogmanPath = Path.Combine(Environment.SystemDirectory, "logman.exe");
    private static readonly object Gate = new();
    private static Owner _owner;

    public static Owner CurrentOwner { get { lock (Gate) return _owner; } }

    private static string MarkerPath => AppPaths.GetPath("kernel-logger-session.marker");

    // Earlier builds' page-fault marker held only a timestamp - nothing to match a session against.
    private static string LegacyMarkerPath => AppPaths.GetPath("pagefault-trace-armed.marker");

    /// <summary>Claims the logger for <paramref name="owner"/>; false (with the current holder)
    /// when another feature of this app already holds it.</summary>
    public static bool TryClaim(Owner owner, out Owner holder)
    {
        lock (Gate)
        {
            holder = _owner;
            if (_owner != Owner.None) return false;
            _owner = owner;
            return true;
        }
    }

    public static void Release(Owner owner)
    {
        lock (Gate)
        {
            if (_owner == owner) _owner = Owner.None;
        }
    }

    /// <summary>Written just before `create`, so even a create that timed out after starting the
    /// session can be reclaimed later - the marker can only ever match this file.</summary>
    public static void RecordStarting(string etlPath)
    {
        try { File.WriteAllText(MarkerPath, etlPath); } catch { /* best-effort */ }
    }

    public static void RecordStopped()
    {
        try { File.Delete(MarkerPath); } catch { /* best-effort */ }
    }

    /// <summary>logman's "already exists" text, or its HRESULT form (0x800700B7) for a localized
    /// Windows where the text differs.</summary>
    public static bool LooksLikeAlreadyRunning(string logmanOutput) =>
        logmanOutput.Contains("already", StringComparison.OrdinalIgnoreCase)
        || logmanOutput.Contains("800700B7", StringComparison.OrdinalIgnoreCase);

    /// <summary>Call while holding the claim, before `create`: stops a kernel session an earlier
    /// run of this app leaked, and only that - see rule 2 in the class remarks.</summary>
    public static async Task ReclaimLeakedSessionAsync()
    {
        try { if (File.Exists(LegacyMarkerPath)) File.Delete(LegacyMarkerPath); } catch { /* best-effort */ }

        string etlPath;
        try
        {
            if (!File.Exists(MarkerPath)) return;
            etlPath = File.ReadAllText(MarkerPath).Trim();
        }
        catch { return; }

        string fileName = Path.GetFileName(etlPath);
        if (!etlPath.EndsWith(".etl", StringComparison.OrdinalIgnoreCase) || fileName.Length < 12)
        {
            RecordStopped(); // not a marker this build wrote - nothing it could identify
            return;
        }

        (string Output, int? ExitCode) query;
        try { query = await ToolRunner.RunCapturedAsync(LogmanPath, $"query \"{Name}\" -ets", 15000); }
        catch { return; }
        if (query.ExitCode is null) return;

        if (query.Output.Contains(fileName, StringComparison.OrdinalIgnoreCase))
        {
            (string Output, int? ExitCode) stop;
            try { stop = await ToolRunner.RunCapturedAsync(LogmanPath, $"stop \"{Name}\" -ets", 15000); }
            catch { return; }
            if (stop.ExitCode != 0) return;
        }
        RecordStopped();
    }

    /// <summary>Synchronous, bounded stop for app shutdown - a no-op unless
    /// <paramref name="owner"/> holds the logger right now.</summary>
    public static void StopBlocking(Owner owner)
    {
        if (CurrentOwner != owner) return;
        try
        {
            var (_, exitCode) = ToolRunner.RunCaptured(LogmanPath, $"stop \"{Name}\" -ets", TimeSpan.FromSeconds(5));
            if (exitCode == 0) RecordStopped();
        }
        catch { /* best-effort - nothing more to do at exit */ }
        Release(owner);
    }
}
