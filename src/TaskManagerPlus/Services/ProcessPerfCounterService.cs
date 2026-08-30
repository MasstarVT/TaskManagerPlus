using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>
/// #410/#412: reads one named counter, per-pid, out of a "Process"-shaped performance counter
/// category ("Process" itself for Page Faults/sec, "Process V2" for Working Set - Private) -
/// every instance in these categories also exposes an "ID Process" counter that maps the
/// (churning, non-stable) instance name back to a real pid, which is what actually lets several
/// instances that happen to share a name (two chrome.exe processes, one showing up as "chrome"
/// and the other as "chrome#1") get summed onto the right row instead of guessed at from the
/// name alone.
///
/// Implementation note: this reads the whole category with ONE
/// PerformanceCounterCategory.ReadCategory() call per tick and computes values from the returned
/// CounterSamples. The original implementation held two PerformanceCounter objects per instance
/// (~700 for a ~350-process "Process" category) and called NextValue() on each - but every raw
/// NextValue() re-reads the entire category from the provider, so one tick cost ~700 full
/// category reads (measured 4-9 seconds per tick on a 24-thread machine; it, not any of the
/// genuinely-per-process syscalls, was why the Processes tab took ~10s to first populate and
/// refreshed far slower than its configured interval). ReadCategory returns every instance's
/// samples in that single read; CounterSample.Calculate does the type-correct math, including
/// the two-sample delta a rate counter needs (the previous tick's samples are kept per resolved
/// pid - not per instance name, which Windows renumbers on same-named-process exit - so a rate
/// reads 0 on the first tick a pid is seen, same behavior the old priming produced).
/// </summary>
public sealed class ProcessPerfCounterService : IDisposable
{
    private readonly string _categoryName;
    private readonly string _valueCounterName;
    private readonly bool _isRate;
    // Round 18, #1030: keyed by resolved pid, NOT by instance name - Windows renumbers same-named
    // instances when one of them exits ("chrome#2" becomes "chrome#1"), so a name-keyed previous
    // sample would be delta'd against a *different* process's raw counter, attributing a garbage
    // rate spike to an innocent pid (which MemoryViewModel then latches as that pid's Peak).
    private readonly Dictionary<int, CounterSample> _previousSamples = new();

    /// <param name="categoryName">"Process" or "Process V2".</param>
    /// <param name="valueCounterName">e.g. "Page Faults/sec" or "Working Set - Private".</param>
    /// <param name="isRate">True for a rate counter (first sample of a new instance reads 0).</param>
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
            var category = new PerformanceCounterCategory(_categoryName).ReadCategory();
            var idData = category["ID Process"];
            var valueData = category[_valueCounterName];
            if (idData is null || valueData is null) return result;

            var seenPids = new HashSet<int>();
            foreach (InstanceData instance in valueData.Values)
            {
                string name = instance.InstanceName;
                // "_Total" is a synthetic aggregate instance both "Process" and "Process V2"
                // expose - it doesn't map to any real pid, so it's skipped rather than
                // mis-attributed. "Idle" reports pid 0, which the pid<=0 guard below drops.
                if (name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;

                if (idData[name] is not { } idInstance) continue;
                int pid = (int)idInstance.Sample.RawValue;
                if (pid <= 0) continue;
                seenPids.Add(pid);

                var sample = instance.Sample;
                double value;
                if (_isRate)
                {
                    // A rate needs last tick's sample for this same pid (#1030: pid, not the
                    // churning instance name - see _previousSamples' remarks); a brand-new pid
                    // reads 0 this tick and a real value from the next one on.
                    value = _previousSamples.TryGetValue(pid, out var previous)
                        ? CounterSample.Calculate(previous, sample)
                        : 0;
                    _previousSamples[pid] = sample;
                }
                else
                {
                    value = CounterSample.Calculate(sample);
                }

                result[pid] = result.TryGetValue(pid, out var existing) ? existing + value : value;
            }

            if (_isRate)
            {
                foreach (var stale in _previousSamples.Keys.Where(k => !seenPids.Contains(k)).ToList())
                    _previousSamples.Remove(stale);
            }
        }
        catch
        {
            // The category can be entirely missing (e.g. "Process V2" pre-Windows 8, or a
            // perf-counter-disabled system) - degrade to "no data" rather than failing the caller.
        }
        return result;
    }

    public void Dispose() => _previousSamples.Clear();
}
