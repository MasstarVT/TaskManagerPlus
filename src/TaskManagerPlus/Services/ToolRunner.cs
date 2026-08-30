using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>
/// The one shared "run a Windows tool, capture its output, kill it on timeout" implementation
/// (Round 18, #1084) - the same consolidation CategorySampler already modeled for performance
/// counters, applied to the RunCaptured/RunCapturedSync/RunCapturedAsync/RunProcessAsync copies
/// that had been pasted into ~40 services and drifted: some killed a timed-out tool with
/// Kill(entireProcessTree: true), some with bare Kill(), so a timed-out tool's children (conhost,
/// a spawned powershell, ...) survived in some services and not others. The per-service wrappers
/// still exist as one-line adapters - a per-service default timeout and a per-service timed-out
/// sentinel are real, load-bearing variation - but the mechanism lives only here, and the timeout
/// kill is always the whole process tree.
///
/// Why each piece of the shape matters (the original PowerPlanService.RunCapturedAsync remarks,
/// preserved): both streams are read concurrently via ReadToEndAsync *before* waiting for exit,
/// because reading one to completion first is the classic .NET Process redirection deadlock (the
/// OS pipe buffers are small and fixed-size - if the child fills one while nothing is draining
/// it, the child blocks writing and the parent blocks reading, forever); the wait is bounded so a
/// wedged tool can't hang its caller; and a genuine timeout is handled as data (ExitCode: null)
/// rather than an exception.
/// </summary>
internal static class ToolRunner
{
    /// <summary>
    /// Runs <paramref name="exe"/> and captures combined stdout+stderr under a real timeout.
    /// A null ExitCode means the run timed out (or <paramref name="ct"/> fired) and the process
    /// tree was killed; Output is then <paramref name="timeoutOutput"/>, never partial output.
    /// A start failure (tool not present on this Windows edition) throws - callers that want
    /// soft degradation instead wrap the call in their own try/catch. Pass
    /// <paramref name="includeStderr"/> false to capture stdout only; stderr is still drained
    /// either way so the child can never block on a full pipe.
    /// </summary>
    public static async Task<(string Output, int? ExitCode)> RunCapturedAsync(
        string exe, string args, int timeoutMs, CancellationToken ct = default,
        string timeoutOutput = "(command timed out)", bool includeStderr = true)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

        var outputTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errorTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return (timeoutOutput, null);
        }

        string stdout = await outputTask;
        string stderr = await errorTask;
        return (includeStderr ? stdout + stderr : stdout, proc.ExitCode);
    }

    /// <summary>
    /// Synchronous twin of <see cref="RunCapturedAsync"/> for callers already running on a worker
    /// thread (the Security tab's scan services all invoke their sync wrappers under Task.Run).
    /// Same semantics, except the timed-out Output defaults to empty because that is what every
    /// sync copy this consolidates returned.
    /// </summary>
    public static (string Output, int? ExitCode) RunCaptured(
        string exe, string args, TimeSpan timeout, string timeoutOutput = "")
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return (timeoutOutput, null);
        }

        return (outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult(), proc.ExitCode);
    }
}
