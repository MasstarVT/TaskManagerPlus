using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>
/// #472/#474/#475: device-tree actions (remove/enable/disable/restart) via pnputil.exe's
/// `/remove-device`, `/enable-device`, `/disable-device`, `/restart-device` verbs (added in
/// Windows 10 2004 and present on every Windows 11 build) - matches this app's "known Windows
/// tool over raw interop" convention (SetupDiCallClassInstaller with DIF_REMOVE/DIF_PROPERTYCHANGE
/// would be the raw-interop alternative; pnputil is simpler and is already the house style for
/// every other driver-package action on this tab). Uses the same concurrent-ReadToEndAsync +
/// bounded-WaitForExitAsync + Kill()-on-timeout pattern ScheduledTaskService/PowerPlanService
/// already establish - a longer 30s timeout than those since a device restart/removal can
/// legitimately take a little longer than a plain query.
/// </summary>
public static class PnpUtilService
{
    public static Task<(bool Success, string Message)> RemoveDeviceAsync(string deviceId) => RunAsync("remove-device", deviceId);
    public static Task<(bool Success, string Message)> EnableDeviceAsync(string deviceId) => RunAsync("enable-device", deviceId);
    public static Task<(bool Success, string Message)> DisableDeviceAsync(string deviceId) => RunAsync("disable-device", deviceId);
    public static Task<(bool Success, string Message)> RestartDeviceAsync(string deviceId) => RunAsync("restart-device", deviceId);

    private static async Task<(bool Success, string Message)> RunAsync(string verb, string deviceId)
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("pnputil.exe", $"/{verb} \"{deviceId}\"");
            string trimmed = output.Trim();
            bool success = exitCode == 0;
            return (success, trimmed.Length > 0 ? trimmed : (success ? "Done." : $"pnputil exited with code {exitCode}."));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Shells out and captures combined stdout+stderr, bounded by a real timeout - the same
    /// concurrent-read/bounded-wait/kill-on-timeout pattern established in ScheduledTaskService
    /// and PowerPlanService (see their remarks for why: naive synchronous ReadToEnd()-then-
    /// WaitForExit() can deadlock on a full pipe buffer, and an unchecked WaitForExit(timeout)
    /// bool result lets a genuine timeout surface as an unexpected exception from .ExitCode
    /// instead of a clean, callers-already-handle-it null).
    /// </summary>
    private static async Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 30000)
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

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { /* best-effort */ }
            return ("(command timed out)", null);
        }

        string output = (await outputTask) + (await errorTask);
        return (output, proc.ExitCode);
    }
}
