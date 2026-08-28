using System.Diagnostics;
using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>One reset-toolkit action's result (#576).</summary>
public sealed record StackResetActionResult(string ActionName, bool Success, string Output, DateTime RanAtUtc);

/// <summary>
/// Item #576 (suggestions.md "Proxy, PAC, VPN and Winsock"): groups the five classic "just reset
/// everything" network commands into one toolkit, each individually confirmed by the caller (this
/// service has no confirmation of its own, matching every other disruptive action's "caller owns
/// the prompt" convention in this app - see NetworkViewModel's per-action confirm text for what each
/// one actually does/breaks) and each run appended to a small text log under the same Logs folder
/// every other log/report in this app writes to.
///
/// That log is a plain tab-separated text file
/// (AppPaths.GetPath("Logs", "network-stack-actions.log")), not the existing LoggingService CSV -
/// LoggingService's own file has a fixed per-tick metrics schema (one header row of column names,
/// written once at Start) that a one-off free-form action record ("ran ipconfig /flushdns at
/// 14:32, output: ...") doesn't fit into without either forcing an unrelated schema change onto the
/// metrics logger or fabricating placeholder columns. Same "settings/log/report files live under
/// AppPaths.SettingsDirectory" convention CLAUDE.md documents, just its own distinct file.
/// </summary>
public static class NetworkStackResetService
{
    private const int TimeoutMs = 25000;

    public static Task<StackResetActionResult> ResetIpStackAsync() => RunAsync("Reset TCP/IP stack", "netsh.exe", "int ip reset");
    public static Task<StackResetActionResult> ResetWinsockAsync() => RunAsync("Reset Winsock catalog", "netsh.exe", "winsock reset");
    public static Task<StackResetActionResult> FlushDnsAsync() => RunAsync("Flush DNS resolver cache", "ipconfig.exe", "/flushdns");
    public static Task<StackResetActionResult> ClearArpCacheAsync() => RunAsync("Clear ARP cache", "arp.exe", "-d *");
    public static Task<StackResetActionResult> ResetNetBiosCacheAsync() => RunAsync("Reset NetBIOS name cache", "nbtstat.exe", "-R");

    private static async Task<StackResetActionResult> RunAsync(string actionName, string exe, string args)
    {
        DateTime ranAt = DateTime.Now;
        StackResetActionResult result;
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
            if (proc is null)
            {
                result = new StackResetActionResult(actionName, false, $"Couldn't start {exe}.", ranAt);
            }
            else
            {
                var outputTask = proc.StandardOutput.ReadToEndAsync();
                var errorTask = proc.StandardError.ReadToEndAsync();
                using var cts = new CancellationTokenSource(TimeoutMs);
                try
                {
                    await proc.WaitForExitAsync(cts.Token);
                    string output = ((await outputTask) + (await errorTask)).Trim();
                    result = new StackResetActionResult(actionName, proc.ExitCode == 0, output.Length == 0 ? "(no output)" : output, ranAt);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(); } catch { /* best-effort */ }
                    result = new StackResetActionResult(actionName, false, $"{exe} timed out.", ranAt);
                }
            }
        }
        catch (Exception ex)
        {
            result = new StackResetActionResult(actionName, false, $"Failed: {ex.Message}", ranAt);
        }

        AppendToActionLog(result);
        return result;
    }

    private static void AppendToActionLog(StackResetActionResult result)
    {
        try
        {
            string path = AppPaths.GetPath("Logs", "network-stack-actions.log");
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string flatOutput = result.Output.Replace('\r', ' ').Replace('\n', ' ').Trim();
            string line = $"{result.RanAtUtc:yyyy-MM-dd HH:mm:ss}\t{result.ActionName}\t{(result.Success ? "OK" : "FAILED")}\t{flatOutput}";
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // Best-effort - a failed log write shouldn't stop the action itself from having run.
        }
    }
}
