using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>
/// #410/#412: reads one named counter, per-pid, out of a "Process"-shaped performance counter
/// category ("Process" itself for Page Faults/sec, "Process V2" for Working Set - Private) - the
/// same technique ProcessMonitorService.ReadGpuUsageByPid already uses for the "GPU Engine"
/// category, generalized: every instance in these categories also exposes an "ID Process" counter
/// that maps the (churning, non-stable) instance name back to a real pid, which is what actually
/// lets several instances that happen to share a name (two chrome.exe processes, one showing up
/// as "chrome" and the other as "chrome#1") get summed onto the right row instead of guessed at
/// from the name alone.
///
/// Counter objects are cached per (category, instance) and disposed once their instance name
/// disappears from a fresh GetInstanceNames() call, matching ReadGpuUsageByPid's churn handling.
/// A brand-new instance's rate counter (Page Faults/sec) is primed with one throwaway NextValue()
/// call before being cached - a rate counter's first-ever sample is always 0, so this avoids
/// reporting a false 0 on the *next* tick instead of only this one. "Working Set - Private" is an
/// instantaneous counter (not a rate), so it needs no priming.
/// </summary>
public sealed class ProcessPerfCounterService : IDisposable
{
    private sealed class InstanceCounters
    {
        public required PerformanceCounter IdProcess;
        public required PerformanceCounter Value;
    }

    private readonly string _categoryName;
    private readonly string _valueCounterName;
    private readonly bool _isRate;
    private readonly Dictionary<string, InstanceCounters> _byInstance = new();

    /// <param name="categoryName">"Process" or "Process V2".</param>
    /// <param name="valueCounterName">e.g. "Page Faults/sec" or "Working Set - Private".</param>
    /// <param name="isRate">True for a rate counter (needs priming on first creation).</param>
    public ProcessPerfCounterService(string categoryName, string valueCounterName, bool isRate)
    {
        _categoryName = categoryName;
        _valueCounterName = valueCounterName;
        _isRate = isRate;
    }

    /// <summary>Reads the current value of this service's configured counter for every live
    /// instance in its category, keyed by resolved pid (summed when more than one instance
    /// resolves to the same pid). Best-effort - a category/counter missing entirely on this
    /// machine (older Windows without "Process V2", for instance) degrades to an empty dictionary
    /// rather than throwing.</summary>
    public Dictionary<int, double> ReadByPid()
    {
        var result = new Dictionary<int, double>();
        try
        {
            var instances = new PerformanceCounterCategory(_categoryName).GetInstanceNames();
            var seen = new HashSet<string>(instances);

            foreach (var stale in _byInstance.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _byInstance[stale].IdProcess.Dispose();
                _byInstance[stale].Value.Dispose();
                _byInstance.Remove(stale);
            }

            // "_Total" is a synthetic aggregate instance both "Process" and "Process V2" expose -
            // it doesn't map to any real pid, so it's skipped rather than mis-attributed.
            foreach (var instance in instances)
            {
                if (instance.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;

                if (!_byInstance.TryGetValue(instance, out var counters))
                {
                    try
                    {
                        var idCounter = new PerformanceCounter(_categoryName, "ID Process", instance, readOnly: true);
                        var valueCounter = new PerformanceCounter(_categoryName, _valueCounterName, instance, readOnly: true);
                        if (_isRate) valueCounter.NextValue(); // prime - see class remarks.
                        counters = new InstanceCounters { IdProcess = idCounter, Value = valueCounter };
                        _byInstance[instance] = counters;
                    }
                    catch
                    {
                        // Instance disappeared between GetInstanceNames() and here, or this
                        // category/counter combination doesn't exist on this machine - skip it.
                        continue;
                    }
                    if (_isRate) continue; // this tick's real value comes next tick, once primed.
                }

                int pid;
                double value;
                try
                {
                    pid = (int)counters.IdProcess.NextValue();
                    value = counters.Value.NextValue();
                }
                catch { continue; }

                if (pid <= 0) continue;
                result[pid] = result.TryGetValue(pid, out var existing) ? existing + value : value;
            }
        }
        catch
        {
            // The category can be entirely missing (e.g. "Process V2" pre-Windows 8, or a
            // perf-counter-disabled system) - degrade to "no data" rather than failing the caller.
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var counters in _byInstance.Values)
        {
            counters.IdProcess.Dispose();
            counters.Value.Dispose();
        }
        _byInstance.Clear();
    }
}
