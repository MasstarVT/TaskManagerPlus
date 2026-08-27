using System.Diagnostics;
using System.IO;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Measures how long after boot each startup item's process actually launched (#91) - a real,
/// observed delay rather than Task Manager's own estimated "startup impact" rating. Only works
/// for an item whose process is still running: matches the executable named in the item's Command
/// against Process.GetProcesses() by process name, then computes Process.StartTime minus the
/// system's approximate boot time (Environment.TickCount64, the same approximation
/// EventLogService already uses for its own last-boot correlation). When more than one process
/// shares that name, the one with the smallest plausible (non-negative, under 6 hours) delay is
/// taken as the actual startup-launched instance, since a later relaunch by the user would show a
/// much larger, implausible delay instead.
/// </summary>
public static class StartupDelayService
{
    private static readonly TimeSpan MaxPlausibleDelay = TimeSpan.FromHours(6);

    public static Dictionary<StartupItem, string> ComputeDelays(IEnumerable<StartupItem> items)
    {
        var result = new Dictionary<StartupItem, string>();
        var bootTime = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return result; }

        try
        {
            foreach (var item in items)
                result[item] = MeasureOne(item, processes, bootTime);
        }
        finally
        {
            foreach (var p in processes)
            {
                try { p.Dispose(); } catch { /* best-effort */ }
            }
        }
        return result;
    }

    private static string MeasureOne(StartupItem item, Process[] processes, DateTime bootTime)
    {
        string target = ExtractExeName(item.Command);
        if (target.Length == 0) return "Not currently running";

        TimeSpan? best = null;
        foreach (var p in processes)
        {
            try
            {
                if (!p.ProcessName.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                var delay = p.StartTime - bootTime;
                if (delay < TimeSpan.Zero || delay > MaxPlausibleDelay) continue;
                if (best is null || delay < best.Value) best = delay;
            }
            catch
            {
                // StartTime/ProcessName can throw for a protected or already-exited process -
                // skip that candidate rather than failing the whole scan.
            }
        }

        return best is { } d ? $"{d.TotalSeconds:0.#}s after boot" : "Not currently running";
    }

    /// <summary>Extracts a bare process name (no path, no extension, no arguments) from a startup
    /// entry's raw Command string, which may be a bare path, a quoted path, or a path followed by
    /// arguments - the same three shapes StartupManagerService.Sample() itself already reads from
    /// the registry/Startup folder.</summary>
    private static string ExtractExeName(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return string.Empty;

        string path;
        if (trimmed[0] == '"')
        {
            int end = trimmed.IndexOf('"', 1);
            path = end > 0 ? trimmed[1..end] : trimmed.Trim('"');
        }
        else
        {
            int space = trimmed.IndexOf(' ');
            path = space > 0 ? trimmed[..space] : trimmed;
        }

        try { return Path.GetFileNameWithoutExtension(path); }
        catch { return string.Empty; }
    }
}
