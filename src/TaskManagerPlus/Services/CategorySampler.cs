using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>
/// Reads one performance counter category as a whole, once per tick, and serves individual values
/// by (counter, instance) - the shared replacement for holding a PerformanceCounter object per
/// value. The trap this exists for (same one ProcessPerfCounterService's remarks document with
/// measurements): every raw PerformanceCounter.NextValue() re-reads its ENTIRE category from the
/// provider, so a sampler holding N counters pays N full category reads per tick.
/// HardwareMonitorService alone held ~130 counters across 9 categories - most of its ~70ms/tick
/// was exactly this.
///
/// Value semantics match NextValue(): CounterSample.Calculate handles every counter type,
/// computing rates against the previous tick's sample kept per (counter, instance). A rate's
/// first-ever read returns 0, the same reason the old code primed each counter at construction -
/// callers that need a real value on their first Sample() should Tick() once at construction the
/// way the priming loop used to.
///
/// Degrades to 0 / "not present", never throws: a missing category makes every read return 0,
/// and HasCounter lets callers keep their "this counter doesn't exist on this Windows version"
/// availability flags.
/// </summary>
public sealed class CategorySampler
{
    /// <summary>The instance name .NET uses internally for a single-instance category's data.</summary>
    private const string SingleInstanceName = "systemdiagnosticsperfcounterlibsingleinstance";

    private readonly string _categoryName;
    private readonly Dictionary<(string Counter, string Instance), CounterSample> _previous = new();
    private InstanceDataCollectionCollection? _current;

    public CategorySampler(string categoryName) => _categoryName = categoryName;

    /// <summary>Reads the whole category once. Call exactly once at the top of each tick;
    /// a missing/broken category leaves every subsequent read returning 0.</summary>
    public void Tick()
    {
        try { _current = new PerformanceCounterCategory(_categoryName).ReadCategory(); }
        catch { _current = null; }
    }

    /// <summary>Whether the last Tick() saw this counter at all - the replacement for the old
    /// "TryCreateCounter returned null" availability checks.</summary>
    public bool HasCounter(string counter)
    {
        try { return _current?[counter] is { Count: > 0 }; }
        catch { return false; }
    }

    /// <summary>Every instance name the last Tick() saw for the given counter.</summary>
    public IEnumerable<string> InstanceNames(string counter)
    {
        var names = new List<string>();
        try
        {
            if (_current?[counter] is { } data)
                foreach (InstanceData instance in data.Values)
                    names.Add(instance.InstanceName);
        }
        catch { /* degrade to empty */ }
        return names;
    }

    /// <summary>Current value of one counter instance, with NextValue()-equivalent semantics.
    /// Pass an empty instance for a single-instance category ("Memory", "System", ...). Returns 0
    /// for anything missing.</summary>
    public double Value(string counter, string instance = "")
    {
        try
        {
            var data = _current?[counter];
            if (data is null) return 0;
            string instanceKey = instance.Length == 0 ? SingleInstanceName : instance;
            if (data[instanceKey] is not { } instanceData) return 0;

            var sample = instanceData.Sample;
            var key = (counter, instanceKey);
            bool havePrevious = _previous.TryGetValue(key, out var previous);
            _previous[key] = sample;

            if (havePrevious && previous.CounterType == sample.CounterType)
                return CounterSample.Calculate(previous, sample);
            // First sight: instantaneous types calculate fine from one sample; rate types can't
            // and read 0, same as an unprimed counter's first NextValue().
            try { return CounterSample.Calculate(sample); }
            catch { return 0; }
        }
        catch
        {
            return 0;
        }
    }
}
