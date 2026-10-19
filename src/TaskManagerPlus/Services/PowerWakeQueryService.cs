using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>
/// #478: parses `powercfg /devicequery wake_armed|wake_from_any|wake_programmable` - each returns
/// a plain list of device friendly names, one per line, with no device instance ID at all (a real
/// limitation of the tool itself, not a parsing gap here) - see WakeDeviceInfo's remarks for how
/// the ViewModel best-effort-matches these back to a Win32_PnPEntity device ID. Shells out rather
/// than any WMI/power-policy API, matching PowerPlanService's existing "known tool, no simple WMI
/// class for this" tradeoff for powercfg-backed features.
/// </summary>
public static class PowerWakeQueryService
{
    public sealed record WakeQueryResult(
        HashSet<string> WakeArmed,
        HashSet<string> WakeFromAny,
        HashSet<string> WakeProgrammable);

    public static async Task<WakeQueryResult> ScanAsync()
    {
        var armed = await QueryAsync("wake_armed");
        var fromAny = await QueryAsync("wake_from_any");
        var programmable = await QueryAsync("wake_programmable");
        return new WakeQueryResult(armed, fromAny, programmable);
    }

    private static async Task<HashSet<string>> QueryAsync(string parameter)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (output, exitCode) = await RunCapturedAsync("powercfg.exe", $"/devicequery {parameter}");
            // A timeout (null), a refusal or an error message is not a device list - its text
            // would otherwise be added as a device name.
            if (exitCode != 0) return names;

            foreach (var rawLine in output.Split('\n'))
            {
                string line = rawLine.Trim('\r', '\n', ' ', '\t');
                if (line.Length == 0) continue;
                // powercfg prints "NONE" when no device matches the query.
                if (line.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;
                names.Add(line);
            }
        }
        catch
        {
            // powercfg unavailable/blocked - empty set, same as every other optional data source
            // in this app degrades on failure.
        }
        return names;
    }

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's historical default timeout.</summary>
    private static Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 10000)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
}
