using System.Diagnostics;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #655: `powercfg /waketimers` - active wake timers, one of the two software wake causes shown
/// next to #653's hardware wake-armed device list (the other being wake-enabled scheduled tasks,
/// via ScheduledTaskService.ListWakeEnabledAsync, merged in by EnergyThermalsViewModel). Confirmed
/// live on a real dev machine that this command refuses outright without administrator privileges
/// (this app runs elevated throughout, per CLAUDE.md's elevation note, so that's expected to be a
/// non-issue in practice) - its exact per-entry line format could not be captured live for that
/// reason, so this is a tolerant, best-effort line parser: it looks for the same
/// "[PROCESS]/[SERVICE]/[DRIVER] &lt;path&gt;" source tag `/requests` uses (the two reports share
/// the same underlying power-request infrastructure), and falls back to showing the raw line as-is
/// when that shape isn't found, rather than dropping it.
/// </summary>
public static class WakeTimerService
{
    private static readonly Regex SourceTagRegex = new(@"^\[(PROCESS|SERVICE|DRIVER)\]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<(List<WakeSourceRow> Timers, string StatusText)> ReadAsync()
    {
        string output;
        try { (output, _) = await RunProcessAsync("powercfg.exe", "/waketimers", 15000); }
        catch (Exception ex) { return (new List<WakeSourceRow>(), $"Couldn't run powercfg /waketimers: {ex.Message}"); }

        if (output.Contains("administrator", StringComparison.OrdinalIgnoreCase))
            return (new List<WakeSourceRow>(), "powercfg /waketimers needs administrator privileges (this app should already be elevated - try relaunching it).");

        if (output.Contains("no active wake timers", StringComparison.OrdinalIgnoreCase) || output.Trim().Length == 0)
            return (new List<WakeSourceRow>(), "No active wake timers.");

        var rows = new List<WakeSourceRow>();
        string? pendingName = null;
        var detailLines = new List<string>();

        void Flush()
        {
            if (pendingName is null) return;
            rows.Add(new WakeSourceRow { Kind = "Wake timer", Name = pendingName, Detail = string.Join(" ", detailLines).Trim() });
            pendingName = null;
            detailLines.Clear();
        }

        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            var sourceMatch = SourceTagRegex.Match(line);
            if (sourceMatch.Success)
            {
                Flush();
                pendingName = sourceMatch.Groups[2].Value.Trim();
                continue;
            }

            if (pendingName is not null) { detailLines.Add(line); continue; }

            // Doesn't match the expected "[TYPE] ..." tag shape - still surface the raw line
            // rather than silently dropping it, since the exact per-entry format isn't a
            // documented, verified contract (see this class's remarks).
            rows.Add(new WakeSourceRow { Kind = "Wake timer", Name = "(unrecognized entry)", Detail = line });
        }
        Flush();

        return (rows, rows.Count == 0 ? "No active wake timers." : $"{rows.Count} active wake timer(s).");
    }

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism.</summary>
    private static Task<(string Output, int? ExitCode)> RunProcessAsync(string exe, string args, int timeoutMs)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
}
