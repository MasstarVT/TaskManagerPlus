using System.Diagnostics;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #653: wake-armed device inventory + per-device disable action. `powercfg /devicequery
/// wake_armed` (currently configured to wake) intersected with `wake_from_any` (able to wake from
/// any sleep state) - the plain one-name-per-line list this pair of subcommands reports is a
/// documented, stable powercfg output shape (see `powercfg /? devicequery`), confirmed live on a
/// real dev machine, unlike the request/report text this chunk parses elsewhere.
/// </summary>
public static class WakeDeviceService
{
    public static async Task<(List<WakeArmedDevice> Devices, string StatusText)> ReadWakeArmedDevicesAsync()
    {
        var armedTask = RunProcessAsync("powercfg.exe", "/devicequery wake_armed", 15000);
        var fromAnyTask = RunProcessAsync("powercfg.exe", "/devicequery wake_from_any", 15000);
        string armedOutput, fromAnyOutput;
        try
        {
            await Task.WhenAll(armedTask, fromAnyTask);
            armedOutput = armedTask.Result.Output;
            fromAnyOutput = fromAnyTask.Result.Output;
        }
        catch (Exception ex)
        {
            return (new List<WakeArmedDevice>(), $"Couldn't run powercfg /devicequery: {ex.Message}");
        }

        if (armedOutput.Contains("administrator", StringComparison.OrdinalIgnoreCase) ||
            fromAnyOutput.Contains("administrator", StringComparison.OrdinalIgnoreCase))
            return (new List<WakeArmedDevice>(), "powercfg /devicequery needs administrator privileges (this app should already be elevated - try relaunching it).");

        var armed = ParseDeviceNames(armedOutput);
        var fromAny = new HashSet<string>(ParseDeviceNames(fromAnyOutput), StringComparer.OrdinalIgnoreCase);

        var devices = armed
            .Where(fromAny.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new WakeArmedDevice { Name = n })
            .ToList();

        string status = devices.Count == 0
            ? "No devices are currently armed to wake this system from sleep."
            : $"{devices.Count} device(s) armed to wake this system.";
        return (devices, status);
    }

    /// <summary>`powercfg /devicedisablewake &lt;name&gt;` - disables one device from waking the
    /// system, using the exact name reported by wake_armed above (per `powercfg /? devicedisablewake`).</summary>
    public static async Task<(bool Success, string? Error)> DisableWakeAsync(string deviceName)
    {
        try
        {
            var (output, exitCode) = await RunProcessAsync("powercfg.exe", $"/devicedisablewake \"{deviceName}\"", 15000);
            return exitCode == 0 ? (true, null) : (false, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static List<string> ParseDeviceNames(string output) =>
        output.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

    /// <summary>Shells out and captures combined stdout+stderr, bounded by a real timeout - same
    /// concurrent-read/bounded-wait/kill-on-timeout pattern PowerPlanService.RunCapturedAsync
    /// established. Duplicated here rather than shared, matching this app's existing convention of
    /// each shelled-out-tool service owning its own small copy.</summary>
    private static async Task<(string Output, int? ExitCode)> RunProcessAsync(string exe, string args, int timeoutMs)
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
