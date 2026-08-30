using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 21, item 99: "guided repair runner" - sfc /scannow, DISM /Online /Cleanup-Image
/// /RestoreHealth, scheduling chkdsk /f on the system volume, and launching mdsched.exe (Windows
/// Memory Diagnostic). Follows the same "shell a known Windows tool, capture its full text output,
/// let the caller show it" shape as DriverVerifierService/CrashDumpConfigService elsewhere in this
/// domain - the difference here is sfc/DISM are genuinely long-running (minutes), so the shared
/// timeout is much larger than every other shelled-out call in this app, and the caller (
/// StabilityViewModel) is responsible for running these off the UI thread with a busy indicator,
/// same as this chunk's own instructions call for.
///
/// chkdsk itself is deliberately not run interactively: on the system (boot) volume Windows can
/// never lock it for exclusive access while it's running, so `chkdsk /f` always has to ask "would
/// you like to schedule this the next time the system restarts (Y/N)?" - piping "Y" to its stdin
/// is fragile (chkdsk's own prompt text isn't a stable, versioned contract, and a hung pipe would
/// block this app's own process indefinitely). `fsutil dirty set` is the same underlying
/// mechanism Windows itself uses to decide whether autochk should run at the next boot (it just
/// sets the volume's own dirty bit directly, non-interactively, with no prompt to answer at all),
/// so that's what this app uses instead - CLAUDE.md's "prefer a known Windows tool" applies just as
/// much to picking the more reliable of two ways to invoke one.
/// </summary>
public static class SystemRepairService
{
    // sfc/DISM are the only genuinely long-running actions in this whole app (CLAUDE.md's other
    // shelled-out calls all finish in well under a minute) - a generous ceiling rather than the
    // 10-20 second timeouts used everywhere else, since killing either mid-scan would be worse
    // than just waiting.
    private const int RepairTimeoutMs = 45 * 60 * 1000; // 45 minutes

    /// <summary>Item 99: `sfc /scannow` - verifies and repairs protected system files. Exit code 0
    /// covers both "no integrity violations found" and "violations found and successfully
    /// repaired"; sfc's own textual summary (in Output) is what actually distinguishes the two, so
    /// the caller shows the full output, not just the pass/fail flag.</summary>
    public static Task<(bool Ok, string Output)> RunSfcAsync() =>
        RunCapturedAsync("sfc.exe", "/scannow", RepairTimeoutMs);

    /// <summary>Item 99: `DISM /Online /Cleanup-Image /RestoreHealth` - repairs the Windows
    /// component store itself (what sfc's own repairs pull clean copies from), using Windows
    /// Update as the source by default. Worth running before sfc when sfc alone reports it
    /// couldn't repair everything.</summary>
    public static Task<(bool Ok, string Output)> RunDismRestoreHealthAsync() =>
        RunCapturedAsync("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", RepairTimeoutMs);

    /// <summary>Item 99: marks the system volume's dirty bit so autochk runs a full disk check the
    /// next time Windows boots, before the desktop loads - the non-interactive equivalent of
    /// answering "Y" to `chkdsk /f`'s own "schedule this for next restart?" prompt. Does not run
    /// chkdsk now (it can't, on a volume Windows itself is running from) and does not restart the
    /// machine - purely schedules the check for whenever the next restart happens.</summary>
    public static async Task<(bool Ok, string Message)> ScheduleChkdskOnSystemVolumeAsync()
    {
        string volume = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
        var (ok, output) = await RunCapturedAsync("fsutil.exe", $"dirty set {volume}", 10000);
        return ok
            ? (true, $"{volume} marked dirty - a full disk check (chkdsk/autochk) will run automatically the next time this machine restarts, before Windows finishes starting.")
            : (false, $"Couldn't schedule a disk check: {output.Trim()}");
    }

    /// <summary>Item 99: launches the interactive Windows Memory Diagnostic tool (mdsched.exe) -
    /// this app only ever launches it (it's a self-contained wizard that schedules its own restart
    /// and runs entirely outside Windows, before any OS loads), never drives it programmatically.
    /// Results are read back afterwards via EventLogService.ReadMemoryDiagnosticsResults, not
    /// returned here.</summary>
    public static bool LaunchMemoryDiagnostic()
    {
        try
        {
            Process.Start(new ProcessStartInfo("mdsched.exe") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>#1084: delegates to the shared <see cref="ToolRunner"/>; this wrapper keeps the
    /// repair flow's soft-start degradation (a tool that can't start reports failure text, never
    /// throws) and its human-readable timeout message.</summary>
    private static async Task<(bool Ok, string Output)> RunCapturedAsync(string exe, string args, int timeoutMs)
    {
        (string Output, int? ExitCode) result;
        try { result = await ToolRunner.RunCapturedAsync(exe, args, timeoutMs); }
        catch (Exception ex) { return (false, $"Couldn't start {exe}: {ex.Message}"); }
        if (result.ExitCode is null)
            return (false, $"{exe} {args} timed out after {timeoutMs / 60000} minute(s) and was stopped.");
        return (result.ExitCode == 0, result.Output);
    }
}
