using System.Diagnostics;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #280: standby (reclaimable file-cache) list depletion - Memory\Free &amp; Zero Page List Bytes
/// plus the three Standby Cache priority-tier counters (Core/Normal Priority/Reserve, which
/// together make up the full standby list - HardwareMonitorService's own StandbyGb/StandbyPercent
/// already sum the same three counters for the Memory tab's "Standby list" meter; this is a
/// separate PerformanceCounter instance reading the same category, since counter objects aren't
/// meant to be shared across services sampling on independent cadences) and Memory\Modified Page
/// List Bytes.
///
/// A collapsed standby list combined with a rising hard-fault rate (#278) means the machine is
/// thrashing its cache and will hitch on every file touch - that combination is flagged in
/// ResponsivenessViewModel.SampleLight, which has both this service's output and PageFaultService's
/// hard-fault rate on hand each tick; this service itself only ever returns raw counter bytes,
/// never a heuristic, keeping "degrade to Unknown/hidden, never fabricate" and "quick flag, not a
/// verdict" cleanly separated (raw data vs. inference).
///
/// Existence-checked defensively per counter (PerformanceCounterCategory.CounterExists before
/// construction), matching PerCoreDpcService.TryInitQueueCounters' pattern - a missing individual
/// counter degrades that one field to 0 rather than failing the whole read.
/// </summary>
public sealed class StandbyListService : IDisposable
{
    private const string Category = "Memory";

    private readonly PerformanceCounter? _freeZeroCounter;
    private readonly PerformanceCounter? _standbyCoreCounter;
    private readonly PerformanceCounter? _standbyNormalCounter;
    private readonly PerformanceCounter? _standbyReserveCounter;
    private readonly PerformanceCounter? _modifiedCounter;

    /// <summary>True once at least one of the three Standby Cache tier counters is available -
    /// those are the counters that actually make up the standby list; Free &amp; Zero/Modified are
    /// read too (both requested by the item) but a machine still has a meaningful standby-list
    /// reading without them.</summary>
    public bool IsAvailable { get; }

    public StandbyListService()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(Category)) return;

            _freeZeroCounter = TryCreate("Free & Zero Page List Bytes");
            _standbyCoreCounter = TryCreate("Standby Cache Core Bytes");
            _standbyNormalCounter = TryCreate("Standby Cache Normal Priority Bytes");
            _standbyReserveCounter = TryCreate("Standby Cache Reserve Bytes");
            _modifiedCounter = TryCreate("Modified Page List Bytes");

            IsAvailable = _standbyCoreCounter is not null || _standbyNormalCounter is not null || _standbyReserveCounter is not null;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    private static PerformanceCounter? TryCreate(string counterName)
    {
        try
        {
            if (!PerformanceCounterCategory.CounterExists(counterName, Category)) return null;
            var c = new PerformanceCounter(Category, counterName, readOnly: true);
            _ = c.NextValue();
            return c;
        }
        catch
        {
            return null;
        }
    }

    public StandbyListInfo Sample()
    {
        if (!IsAvailable)
            return new StandbyListInfo { IsAvailable = false, StatusText = "Standby-list performance counters aren't available on this system." };
        try
        {
            return new StandbyListInfo
            {
                IsAvailable = true,
                FreeZeroBytes = ReadLong(_freeZeroCounter),
                StandbyCoreBytes = ReadLong(_standbyCoreCounter),
                StandbyNormalBytes = ReadLong(_standbyNormalCounter),
                StandbyReserveBytes = ReadLong(_standbyReserveCounter),
                ModifiedPageListBytes = ReadLong(_modifiedCounter),
            };
        }
        catch
        {
            return new StandbyListInfo { IsAvailable = false, StatusText = "Read failed on this tick." };
        }
    }

    private static long ReadLong(PerformanceCounter? c)
    {
        if (c is null) return 0;
        try { return (long)Math.Max(0, c.NextValue()); }
        catch { return 0; }
    }

    public void Dispose()
    {
        _freeZeroCounter?.Dispose();
        _standbyCoreCounter?.Dispose();
        _standbyNormalCounter?.Dispose();
        _standbyReserveCounter?.Dispose();
        _modifiedCounter?.Dispose();
    }
}
