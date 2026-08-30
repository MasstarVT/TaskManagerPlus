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
/// first-ever read returns 0 unless the sampler was primed - callers that need a real value on
/// their first Sample() should Tick() once at construction the way the priming loop used to.
/// Round 18, #1029: priming actually works now - Tick() folds the outgoing snapshot into the
/// previous-sample table the first time around, so the ctor's Tick() is no longer discarded by
/// the first Sample()'s own Tick() (which used to leave every rate counter reading 0 on the
/// first UI sample despite the priming).
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
    private readonly Dictionary<(string Counter, string Instance), CounterSample> _previous = new(CounterKeyComparer.Instance);
    private InstanceDataCollectionCollection? _current;

    public CategorySampler(string categoryName) => _categoryName = categoryName;

    /// <summary>Reads the whole category once. Call exactly once at the top of each tick;
    /// a missing/broken category leaves every subsequent read returning 0.</summary>
    public void Tick()
    {
        var outgoing = _current;
        try { _current = new PerformanceCounterCategory(_categoryName).ReadCategory(); }
        catch { _current = null; }

        // Round 18, #1029: the first time a snapshot is replaced before any Value() call recorded
        // previous samples from it (i.e. the ctor's priming Tick()), fold the whole outgoing
        // snapshot into the previous-sample table so the first real Sample() can compute genuine
        // rates instead of 0 for every rate counter. Steady state is untouched - once Value()
        // has populated _previous this never runs again, so it costs one walk of one snapshot
        // per sampler lifetime, not per tick.
        if (_previous.Count == 0 && outgoing is not null)
        {
            try
            {
                foreach (System.Collections.DictionaryEntry entry in outgoing)
                {
                    if (entry.Value is not InstanceDataCollection counterData) continue;
                    string counterName = counterData.CounterName;
                    foreach (InstanceData instanceData in counterData.Values)
                        _previous[(counterName, instanceData.InstanceName)] = instanceData.Sample;
                }
            }
            catch { /* degrade to unprimed (first rate reads 0), same as before this fix */ }
        }
    }

    /// <summary>Round 18, #1029: case-insensitive on both parts - the counter names the provider
    /// reports into ReadCategory's collections (folded into _previous by Tick's priming pass) and
    /// the names callers pass to Value() must land on the same key even if their casing differs
    /// (the underlying InstanceDataCollectionCollection indexer is itself case-insensitive).</summary>
    private sealed class CounterKeyComparer : IEqualityComparer<(string Counter, string Instance)>
    {
        public static readonly CounterKeyComparer Instance = new();

        public bool Equals((string Counter, string Instance) x, (string Counter, string Instance) y) =>
            string.Equals(x.Counter, y.Counter, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Instance, y.Instance, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Counter, string Instance) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Counter),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Instance));
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
