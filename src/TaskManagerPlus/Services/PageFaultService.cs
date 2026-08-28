using System.Diagnostics;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #278/#279 (fallback mode): hard-fault rate - system-wide (Memory\Pages Input/sec, Memory\Page
/// Reads/sec) and a per-process approximation (Process(*)\Page Faults/sec).
///
/// #278: Memory\Pages Input/sec is the counter that specifically reflects pages actually read from
/// disk to resolve a fault - unlike Memory\Page Faults/sec (soft+hard combined), which is dominated
/// by harmless soft faults resolved straight from RAM (nearly every process generates thousands of
/// these) and would be useless as a hitching signal on its own. Memory\Page Reads/sec is the
/// underlying disk-read rate behind those page-ins (one disk read can satisfy more than one page,
/// so this can read lower than Pages Input/sec) - both are surfaced since either can be the more
/// useful number depending on whether the reader wants "how much data is being pulled back from
/// disk" or "how many separate I/Os this is costing".
///
/// #279 fallback: Windows exposes no per-process hard-fault-only performance counter - the closest
/// always-available per-process figure is Process(*)\Page Faults/sec, which (like the system-wide
/// Memory\Page Faults/sec above) is soft+hard combined. This is surfaced explicitly labeled
/// "approximate" in the UI/tooltip; HardFaultEtwService's optional deep mode is the only way to get
/// genuinely hard-fault-only, per-process (and per-file) attribution.
///
/// Both halves independently degrade to IsAvailable=false / an empty list on a missing counter
/// category - never a fabricated number, per CLAUDE.md's "degrade to hidden" rule. Existence is
/// checked once at construction (PerformanceCounterCategory.Exists/CounterExists), matching
/// PerCoreDpcService.TryInitQueueCounters' defensive pattern. Rides ResponsivenessViewModel's cheap
/// always-on _lightTimer (2s) - every read here is a plain PerformanceCounter sample, the same tier
/// as HardwareMonitorService's own counters.
/// </summary>
public sealed class PageFaultService : IDisposable
{
    private PerformanceCounter? _pagesInputCounter;
    private PerformanceCounter? _pageReadsCounter;
    public bool HardFaultRateAvailable { get; private set; }

    // #279: previous raw "Page Faults/sec" reading per process instance, for the rate the
    // counter's own NextValue() would otherwise compute for us - see SampleTopProcesses for why
    // this reads the category directly instead. Keyed by instance name; the PID is stored
    // alongside so a recycled instance name ("chrome#7" becoming a different process) resets its
    // baseline instead of reporting a nonsense spike from subtracting an unrelated process's
    // counter.
    private readonly Dictionary<string, (int Pid, long RawFaults, DateTime AtUtc)> _faultBaseline = new();
    public bool PerProcessAvailable { get; private set; }

    public PageFaultService()
    {
        TryInitHardFaultRate();
        try
        {
            PerProcessAvailable = PerformanceCounterCategory.Exists("Process") &&
                                   PerformanceCounterCategory.CounterExists("Page Faults/sec", "Process");
        }
        catch
        {
            PerProcessAvailable = false;
        }
    }

    private void TryInitHardFaultRate()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists("Memory")) { HardFaultRateAvailable = false; return; }

            if (PerformanceCounterCategory.CounterExists("Pages Input/sec", "Memory"))
            {
                _pagesInputCounter = new PerformanceCounter("Memory", "Pages Input/sec", readOnly: true);
                _ = _pagesInputCounter.NextValue();
            }
            if (PerformanceCounterCategory.CounterExists("Page Reads/sec", "Memory"))
            {
                _pageReadsCounter = new PerformanceCounter("Memory", "Page Reads/sec", readOnly: true);
                _ = _pageReadsCounter.NextValue();
            }
            HardFaultRateAvailable = _pagesInputCounter is not null || _pageReadsCounter is not null;
        }
        catch
        {
            HardFaultRateAvailable = false;
        }
    }

    public HardFaultRateInfo SampleHardFaultRate()
    {
        if (!HardFaultRateAvailable)
            return new HardFaultRateInfo { IsAvailable = false, StatusText = "Memory\\Pages Input/sec and Memory\\Page Reads/sec aren't available on this system." };
        try
        {
            double pagesIn = 0, reads = 0;
            if (_pagesInputCounter is not null) pagesIn = Math.Max(0, _pagesInputCounter.NextValue());
            if (_pageReadsCounter is not null) reads = Math.Max(0, _pageReadsCounter.NextValue());
            return new HardFaultRateInfo { IsAvailable = true, PagesInputPerSec = pagesIn, PageReadsPerSec = reads };
        }
        catch
        {
            return new HardFaultRateInfo { IsAvailable = false, StatusText = "Read failed on this tick." };
        }
    }

    /// <summary>#279 fallback: per-process Process(*)\Page Faults/sec, ranked descending, top N -
    /// see the class remarks for why this is explicitly an approximation.
    ///
    /// Reads the whole "Process" category ONCE per call (PerformanceCounterCategory.ReadCategory)
    /// rather than holding a PerformanceCounter per instance and calling NextValue() on each.
    /// That is not a micro-optimisation: every NextValue() on a multi-instance category re-reads
    /// the entire category blob from the registry/PDH, which for the Process category on a busy
    /// machine is multiple megabytes - large enough to land straight on the Large Object Heap. At
    /// ~400 process instances x 2 counters that was ~800 whole-category reads per call, on
    /// ResponsivenessViewModel's always-on 2s timer, and it dominated this process: a trace showed
    /// SampleLight occupying ~84% of wall clock and the GC holding well over a gigabyte of LOH
    /// garbage, on every tab, whether or not the Responsiveness tab was even open. One
    /// ReadCategory() call returns the same data for every instance and counter at once.
    ///
    /// ReadCategory gives raw counter values, not computed rates, so the per-second figure is
    /// derived here from the delta against the previous sample - which is exactly what
    /// NextValue() was doing internally. The first call after startup (or after an instance
    /// appears) has no baseline to subtract and so reports nothing for it rather than a fabricated
    /// number, per CLAUDE.md's degrade-never-fabricate rule.</summary>
    public List<ProcessPageFaultRow> SampleTopProcesses(int top = 15)
    {
        var rows = new List<ProcessPageFaultRow>();
        if (!PerProcessAvailable) return rows;
        try
        {
            var category = new PerformanceCounterCategory("Process");
            InstanceDataCollectionCollection data = category.ReadCategory();

            var idData = data["ID Process"];
            var faultData = data["Page Faults/sec"];
            if (idData is null || faultData is null) return rows;

            var now = DateTime.UtcNow;
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string inst in faultData.Keys)
            {
                if (inst is "_Total" or "Idle") continue;

                var faultEntry = faultData[inst];
                var idEntry = idData[inst];
                if (faultEntry is null || idEntry is null) continue;

                int pid = unchecked((int)idEntry.RawValue);
                if (pid <= 0) continue;
                long rawFaults = faultEntry.RawValue;
                live.Add(inst);

                bool haveBaseline = _faultBaseline.TryGetValue(inst, out var prev);
                _faultBaseline[inst] = (pid, rawFaults, now);

                // No baseline, a recycled instance name, or a counter that went backwards (process
                // restarted under the same instance name) - record the baseline and report nothing
                // for this instance this pass rather than a bogus rate.
                if (!haveBaseline || prev.Pid != pid || rawFaults < prev.RawFaults) continue;

                double seconds = (now - prev.AtUtc).TotalSeconds;
                if (seconds <= 0) continue;

                double faultsPerSec = (rawFaults - prev.RawFaults) / seconds;

                // Instance names are the bare process name, or "name#N" for the Nth instance of a
                // process with multiple running copies - strip the suffix for display.
                string name = inst.Contains('#') ? inst[..inst.IndexOf('#')] : inst;
                rows.Add(new ProcessPageFaultRow { Pid = pid, ProcessName = name, PageFaultsPerSec = Math.Round(Math.Max(0, faultsPerSec), 0) });
            }

            PruneStale(live);
        }
        catch
        {
            return new List<ProcessPageFaultRow>();
        }
        return rows.OrderByDescending(r => r.PageFaultsPerSec).Take(top).ToList();
    }

    /// <summary>Drops baselines for process instances that no longer exist, so this dictionary
    /// tracks the live process set rather than growing for the life of the app.</summary>
    private void PruneStale(HashSet<string> live)
    {
        if (_faultBaseline.Count == live.Count) return;
        foreach (var key in _faultBaseline.Keys.ToList())
            if (!live.Contains(key)) _faultBaseline.Remove(key);
    }

    public void Dispose()
    {
        _pagesInputCounter?.Dispose();
        _pageReadsCounter?.Dispose();
        _faultBaseline.Clear();
    }
}
