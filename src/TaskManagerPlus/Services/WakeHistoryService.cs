using System.Diagnostics;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #654: wake-history attribution - combines `powercfg /lastwake` (Windows' own one-line summary
/// of the single most recent wake) with the richer Kernel-Power 107 / Power-Troubleshooter event-1
/// event-log entries EventLogService.ReadWakeHistoryEvents already reads, since /lastwake only ever
/// reports the single most recent transition while the event log carries real history. Confirmed
/// live on a real dev machine: `/lastwake` prints a plain "Wake History Count - N" / "Wake Source
/// Count - N" summary (0 sources is a common, honest result, not a parsing gap), and
/// Power-Troubleshooter event 1's message is "The system has returned from a low power state." with
/// "Sleep Time:", "Wake Time:", and "Wake Source:" lines - "Unknown" is the real, live value Windows
/// itself reports there when it can't attribute a wake to a specific device.
/// </summary>
public static class WakeHistoryService
{
    private static readonly Regex WakeSourceCountRegex = new(@"Wake Source Count\s*-\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<(List<WakeHistoryEntry> Entries, string LastWakeSummary)> ReadAsync(EventLogService eventLog)
    {
        var entries = await Task.Run(() => eventLog.ReadWakeHistoryEvents());

        string lastWakeSummary;
        try
        {
            var (output, _) = await RunProcessAsync("powercfg.exe", "/lastwake", 10000);
            lastWakeSummary = output.Contains("administrator", StringComparison.OrdinalIgnoreCase)
                ? "powercfg /lastwake needs administrator privileges."
                : SummarizeLastWake(output);
        }
        catch (Exception ex)
        {
            lastWakeSummary = $"Couldn't run powercfg /lastwake: {ex.Message}";
        }

        return (entries, lastWakeSummary);
    }

    private static string SummarizeLastWake(string output)
    {
        var match = WakeSourceCountRegex.Match(output);
        if (!match.Success) return output.Trim().Length == 0 ? "No wake-history data reported." : output.Trim();

        return int.Parse(match.Groups[1].Value) == 0
            ? "powercfg /lastwake reports no specific wake source for the most recent wake (this is common - Windows often can't attribute a wake to one device)."
            : output.Trim();
    }

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism.</summary>
    private static Task<(string Output, int? ExitCode)> RunProcessAsync(string exe, string args, int timeoutMs)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
}
