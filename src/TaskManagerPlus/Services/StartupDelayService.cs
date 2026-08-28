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
///
/// Round 8 #22 extends this same scan with a combined "startup impact score": for whichever items
/// matched a still-running process above, a quick CPU/memory footprint sample of that process is
/// taken (two TotalProcessorTime reads separated by one short shared wait - the same "two samples,
/// one elapsed window" technique ProcessMonitorService's own per-tick CPU% already uses) and
/// blended with the measured delay into a Low/Medium/High bucket. The wait is taken exactly once
/// per scan (not once per item), so a long startup list doesn't stack into a multi-second delay.
/// </summary>
public static class StartupDelayService
{
    private static readonly TimeSpan MaxPlausibleDelay = TimeSpan.FromHours(6);

    // #22: how long to wait between the two CPU-time samples used for the impact score's CPU%
    // reading - short enough that a startup list scan still feels instant, long enough that a
    // genuinely busy background process shows a nonzero delta.
    private const int ImpactSampleWindowMs = 250;

    public static Dictionary<StartupItem, StartupMeasurement> ComputeDelays(IEnumerable<StartupItem> items)
    {
        var result = new Dictionary<StartupItem, StartupMeasurement>();
        var bootTime = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return result; }

        try
        {
            var itemList = items as IList<StartupItem> ?? items.ToList();
            var delayTexts = new Dictionary<StartupItem, string>();
            var delays = new Dictionary<StartupItem, TimeSpan>();
            var matched = new Dictionary<StartupItem, Process>();

            foreach (var item in itemList)
            {
                var (text, delay, process) = MeasureOne(item, processes, bootTime);
                delayTexts[item] = text;
                if (delay is { } d) delays[item] = d;
                if (process is not null) matched[item] = process;
            }

            // One shared CPU-time sampling window across every matched process, rather than one
            // per item - see the class remarks above.
            var cpuT0 = new Dictionary<StartupItem, TimeSpan>();
            foreach (var (item, proc) in matched)
            {
                try { cpuT0[item] = proc.TotalProcessorTime; } catch { /* protected/exited - skip */ }
            }

            var sampleStart = DateTime.UtcNow;
            if (cpuT0.Count > 0) Thread.Sleep(ImpactSampleWindowMs);
            var elapsedMs = Math.Max(1.0, (DateTime.UtcNow - sampleStart).TotalMilliseconds);

            foreach (var item in itemList)
            {
                string delayText = delayTexts[item];
                string impactText = "Not currently running";
                string impactDetail = string.Empty;

                if (matched.TryGetValue(item, out var proc))
                {
                    double cpuPercent = 0;
                    long memoryBytes = 0;
                    try
                    {
                        if (cpuT0.TryGetValue(item, out var t0))
                        {
                            var deltaMs = (proc.TotalProcessorTime - t0).TotalMilliseconds;
                            cpuPercent = Math.Max(0, deltaMs / elapsedMs / Environment.ProcessorCount * 100.0);
                        }
                        memoryBytes = proc.WorkingSet64;
                    }
                    catch
                    {
                        // Process exited mid-scan or access denied - score with whatever was
                        // gathered (likely just the delay).
                    }

                    double delaySeconds = delays.TryGetValue(item, out var d) ? d.TotalSeconds : 0;
                    (impactText, impactDetail) = ScoreImpact(delaySeconds, cpuPercent, memoryBytes);
                }

                // #748: the numeric delay (when this item matched a running process this scan) is
                // handed to StartupHistoryService by the caller so it can persist a per-item
                // sample history - null for "not currently running", never a fabricated 0.
                double? measuredDelaySeconds = delays.TryGetValue(item, out var delay) ? delay.TotalSeconds : null;
                result[item] = new StartupMeasurement(delayText, impactText, impactDetail, measuredDelaySeconds);
            }
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

    /// <summary>
    /// Blends measured boot delay, a brief CPU% sample, and current working-set memory into one
    /// Low/Medium/High bucket (#22) - a coarse, weighted point score, not a precise benchmark: the
    /// CPU reading in particular is a single short sample of whatever the process happens to be
    /// doing right now, not an average over its actual startup window (which would need continuous
    /// tracking from the moment it launched, well beyond what this on-demand scan can measure).
    /// </summary>
    private static (string ImpactText, string Detail) ScoreImpact(double delaySeconds, double cpuPercent, long memoryBytes)
    {
        double memoryMb = memoryBytes / 1024.0 / 1024.0;

        int points = 0;
        if (delaySeconds >= 10) points += 2;
        else if (delaySeconds >= 3) points += 1;

        if (cpuPercent >= 15) points += 2;
        else if (cpuPercent >= 5) points += 1;

        if (memoryMb >= 300) points += 2;
        else if (memoryMb >= 100) points += 1;

        string label = points >= 4 ? "High impact" : points >= 2 ? "Medium impact" : "Low impact";
        string detail = $"{delaySeconds:0.#}s delay · {cpuPercent:0.#}% CPU (brief sample) · {memoryMb:0} MB";
        return (label, detail);
    }

    private static (string Text, TimeSpan? Delay, Process? Process) MeasureOne(StartupItem item, Process[] processes, DateTime bootTime)
    {
        string target = ExtractExeName(item.Command);
        if (target.Length == 0) return ("Not currently running", null, null);

        TimeSpan? best = null;
        Process? bestProcess = null;
        foreach (var p in processes)
        {
            try
            {
                if (!p.ProcessName.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                var delay = p.StartTime - bootTime;
                if (delay < TimeSpan.Zero || delay > MaxPlausibleDelay) continue;
                if (best is null || delay < best.Value)
                {
                    best = delay;
                    bestProcess = p;
                }
            }
            catch
            {
                // StartTime/ProcessName can throw for a protected or already-exited process -
                // skip that candidate rather than failing the whole scan.
            }
        }

        return best is { } d
            ? ($"{d.TotalSeconds:0.#}s after boot", d, bestProcess)
            : ("Not currently running", null, null);
    }

    /// <summary>Reduces StartupManagerService.ExtractPath's result down to a bare process name (no
    /// path, no extension, no arguments) for matching against Process.GetProcesses().</summary>
    private static string ExtractExeName(string command)
    {
        string path = StartupManagerService.ExtractPath(command);
        if (path.Length == 0) return string.Empty;

        try { return Path.GetFileNameWithoutExtension(path); }
        catch { return string.Empty; }
    }
}

/// <summary>Result of one StartupDelayService.ComputeDelays scan for a single item - the measured
/// boot delay text (#91) plus the combined impact score (#22), plus the raw numeric delay in
/// seconds (#748, null when the item isn't currently running) for StartupHistoryService to persist.</summary>
public sealed record StartupMeasurement(string DelayText, string ImpactText, string ImpactDetailText, double? DelaySeconds);
