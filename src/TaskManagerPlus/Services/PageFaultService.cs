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

    // Cached per-instance PerformanceCounter objects, same "construct lazily, prune when the
    // instance disappears" shape DotNetPerfCounterService.ReadCounter/PruneStaleCounters use for
    // the same kind of process-instance churn.
    private readonly Dictionary<string, PerformanceCounter> _processCounters = new();
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
    /// see the class remarks for why this is explicitly an approximation.</summary>
    public List<ProcessPageFaultRow> SampleTopProcesses(int top = 15)
    {
        var rows = new List<ProcessPageFaultRow>();
        if (!PerProcessAvailable) return rows;
        try
        {
            var category = new PerformanceCounterCategory("Process");
            var instances = category.GetInstanceNames().Where(n => n != "_Total" && n != "Idle").ToList();
            var liveKeys = new HashSet<string>();

            foreach (var inst in instances)
            {
                int pid = (int)ReadCounter("ID Process", inst, liveKeys);
                if (pid <= 0) continue;
                double faultsPerSec = ReadCounter("Page Faults/sec", inst, liveKeys);

                // Instance names are the bare process name, or "name#N" for the Nth instance of a
                // process with multiple running copies - strip the suffix for display.
                string name = inst.Contains('#') ? inst[..inst.IndexOf('#')] : inst;
                rows.Add(new ProcessPageFaultRow { Pid = pid, ProcessName = name, PageFaultsPerSec = Math.Round(Math.Max(0, faultsPerSec), 0) });
            }

            PruneStale(liveKeys);
        }
        catch
        {
            return new List<ProcessPageFaultRow>();
        }
        return rows.OrderByDescending(r => r.PageFaultsPerSec).Take(top).ToList();
    }

    private double ReadCounter(string counterName, string instance, HashSet<string> liveKeys)
    {
        string key = $"{counterName}|{instance}";
        liveKeys.Add(key);

        if (!_processCounters.TryGetValue(key, out var counter))
        {
            try
            {
                counter = new PerformanceCounter("Process", counterName, instance, readOnly: true);
                _processCounters[key] = counter;
            }
            catch
            {
                return 0;
            }
        }

        try { return counter.NextValue(); }
        catch { return 0; }
    }

    private void PruneStale(HashSet<string> liveKeys)
    {
        foreach (var key in _processCounters.Keys.ToList())
        {
            if (liveKeys.Contains(key)) continue;
            try { _processCounters[key].Dispose(); } catch { /* ignore */ }
            _processCounters.Remove(key);
        }
    }

    public void Dispose()
    {
        _pagesInputCounter?.Dispose();
        _pageReadsCounter?.Dispose();
        foreach (var c in _processCounters.Values) c.Dispose();
        _processCounters.Clear();
    }
}
