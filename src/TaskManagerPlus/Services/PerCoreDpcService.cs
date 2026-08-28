using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #205: per-core DPC/interrupt-time percentage via NtQuerySystemInformation
/// (SystemProcessorPerformanceInformation), which returns DpcTime/InterruptTime/InterruptCount per
/// logical CPU. HardwareMonitorService's existing "% DPC Time"/"% Interrupt Time" PerformanceCounters
/// only read the "_Total" instance (see HardwareSnapshot.CpuDpcPercent/CpuInterruptPercent's
/// remarks) - there's no per-core "Processor(n)\% DPC Time" counter on Windows, so a per-core
/// breakdown needs this lower-level call instead. Diffed between calls (both fields are cumulative
/// 100ns counters since boot), the same diffing approach HardwareMonitorService already uses for
/// its own counters.
///
/// #206: DPC queue depth/rate via the "Processor Information(*)\DPCs Queued/sec" and "\DPC Rate"
/// PerformanceCounters, one pair per logical core - a high queue with low DPC time points at an
/// interrupt storm rather than a slow driver, which the per-driver DPC time table alone can't show.
///
/// #215: interrupt-storm detection - per-core interrupt rate from the same
/// SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION.InterruptCount field #205 already reads (diffed the
/// same way), cross-checked against the "Processor Information(*)\Interrupts/sec" PerformanceCounter
/// when that category is available. A storm produces stutter with almost no visible CPU usage in
/// the normal Task Manager, since the work happens in interrupt/DPC context rather than a process's
/// own CPU time - see SampleInterruptStorm's remarks for the flagging heuristic. "Quick flag, not a
/// verdict."
///
/// Instantiated once by ResponsivenessViewModel and sampled on its own lightweight timer (not
/// gated behind the Start/Stop measurement session - both of these reads are cheap syscalls/perf-
/// counter reads, not an ETW capture, so there's no reason to withhold them while idle).
/// </summary>
public sealed class PerCoreDpcService : IDisposable
{
    private const int SystemProcessorPerformanceInformation = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    private long[]? _prevDpc;
    private long[]? _prevInterrupt;
    private DateTime _prevTime;

    private readonly List<PerformanceCounter> _queuedCounters = new();
    private readonly List<PerformanceCounter> _rateCounters = new();
    private readonly List<PerformanceCounter> _interruptRateCounters = new();
    private readonly List<string> _coreInstanceNames = new();
    private bool _queueCountersReady;

    // #215: separate prev-sample state from #205's SampleCoreDpcInterrupt above, so the two
    // methods can be called independently (in any order, any tick) without entangling each other's
    // diff baseline.
    private long[]? _prevInterruptCount;
    private DateTime _prevInterruptCountTime;

    // Heuristic thresholds - "quick flag, not a verdict": a core sustaining an interrupt rate this
    // many times its siblings' median, or above this absolute ceiling regardless of the other
    // cores, is flagged as a suspected storm. Chosen conservatively (a busy NIC/GPU can legitimately
    // sustain a few thousand interrupts/sec under coalescing) to avoid flagging ordinary load.
    private const double StormRelativeMultiplier = 4.0;
    private const double StormAbsoluteCeilingPerSec = 20_000;

    public PerCoreDpcService()
    {
        TryInitQueueCounters();
    }

    /// <summary>Best-effort - some systems/virtualized environments don't expose the "Processor
    /// Information" category's queue counters at all, in which case SampleQueueRates just returns
    /// an empty list (the #206 tiles stay hidden) rather than throwing.</summary>
    private void TryInitQueueCounters()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists("Processor Information")) return;
            var category = new PerformanceCounterCategory("Processor Information");
            var instances = category.GetInstanceNames()
                .Where(n => n.Contains(',') && !n.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            bool hasInterruptCounter = PerformanceCounterCategory.CounterExists("Interrupts/sec", "Processor Information");

            foreach (var inst in instances)
            {
                _queuedCounters.Add(new PerformanceCounter("Processor Information", "DPCs Queued/sec", inst, readOnly: true));
                _rateCounters.Add(new PerformanceCounter("Processor Information", "DPC Rate", inst, readOnly: true));
                if (hasInterruptCounter)
                    _interruptRateCounters.Add(new PerformanceCounter("Processor Information", "Interrupts/sec", inst, readOnly: true));
                _coreInstanceNames.Add(inst);
            }
            foreach (var c in _queuedCounters) _ = c.NextValue();
            foreach (var c in _rateCounters) _ = c.NextValue();
            foreach (var c in _interruptRateCounters) _ = c.NextValue();
            _queueCountersReady = _queuedCounters.Count > 0;
        }
        catch
        {
            _queueCountersReady = false;
        }
    }

    public List<CoreDpcRow> SampleCoreDpcInterrupt()
    {
        var rows = new List<CoreDpcRow>();
        try
        {
            int procCount = Environment.ProcessorCount;
            int entrySize = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
            int bufSize = entrySize * procCount;
            IntPtr buffer = Marshal.AllocHGlobal(bufSize);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, bufSize, out _);
                if (status != 0) return rows;

                var dpc = new long[procCount];
                var interrupt = new long[procCount];
                for (int i = 0; i < procCount; i++)
                {
                    var s = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(IntPtr.Add(buffer, i * entrySize));
                    dpc[i] = s.DpcTime;
                    interrupt[i] = s.InterruptTime;
                }

                var now = DateTime.UtcNow;
                if (_prevDpc is not null && _prevDpc.Length == procCount && _prevInterrupt is not null)
                {
                    double elapsed100ns = (now - _prevTime).TotalMilliseconds * 10000.0;
                    if (elapsed100ns > 0)
                    {
                        for (int i = 0; i < procCount; i++)
                        {
                            double dpcPct = Math.Clamp((dpc[i] - _prevDpc[i]) / elapsed100ns * 100.0, 0, 100);
                            double intPct = Math.Clamp((interrupt[i] - _prevInterrupt[i]) / elapsed100ns * 100.0, 0, 100);
                            rows.Add(new CoreDpcRow { CoreIndex = i, DpcPercent = dpcPct, InterruptPercent = intPct });
                        }
                    }
                }

                _prevDpc = dpc;
                _prevInterrupt = interrupt;
                _prevTime = now;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            // best-effort - an empty list on this tick just means the chart/grid skips a beat
        }
        return rows;
    }

    /// <summary>#215: per-core interrupt rate for the last sample interval, flagging any core whose
    /// rate is far above its siblings' median or above an absolute ceiling - see the class remarks
    /// for the thresholds. Prefers the "Interrupts/sec" PerformanceCounter (matching the requested
    /// data source) when the "Processor Information" category exposes it; falls back to diffing
    /// SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION.InterruptCount (always available, no perf-counter
    /// category dependency) otherwise - both are the same underlying cumulative interrupt count,
    /// just read via a different API, so either source is equally honest.</summary>
    public List<CoreInterruptRow> SampleInterruptStorm()
    {
        var rates = _interruptRateCounters.Count > 0 ? SampleFromCounters() : SampleFromSyscall();
        if (rates is null || rates.Count == 0) return new List<CoreInterruptRow>();

        var sorted = rates.OrderBy(r => r).ToList();
        double median = sorted[sorted.Count / 2];

        var rows = new List<CoreInterruptRow>(rates.Count);
        for (int i = 0; i < rates.Count; i++)
        {
            double rate = rates[i];
            bool storm = rate >= StormAbsoluteCeilingPerSec ||
                         (median > 50 && rate >= median * StormRelativeMultiplier);
            rows.Add(new CoreInterruptRow { CoreIndex = i, InterruptsPerSec = rate, IsSuspectedStorm = storm });
        }
        return rows;
    }

    private List<double>? SampleFromCounters()
    {
        try
        {
            var rates = new List<double>(_interruptRateCounters.Count);
            foreach (var c in _interruptRateCounters) rates.Add(c.NextValue());
            return rates;
        }
        catch
        {
            return null;
        }
    }

    private List<double>? SampleFromSyscall()
    {
        try
        {
            int procCount = Environment.ProcessorCount;
            int entrySize = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
            int bufSize = entrySize * procCount;
            IntPtr buffer = Marshal.AllocHGlobal(bufSize);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, bufSize, out _);
                if (status != 0) return null;

                var counts = new long[procCount];
                for (int i = 0; i < procCount; i++)
                {
                    var s = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(IntPtr.Add(buffer, i * entrySize));
                    counts[i] = s.InterruptCount;
                }

                var now = DateTime.UtcNow;
                List<double>? rates = null;
                if (_prevInterruptCount is not null && _prevInterruptCount.Length == procCount)
                {
                    double elapsedSec = (now - _prevInterruptCountTime).TotalSeconds;
                    if (elapsedSec > 0)
                    {
                        rates = new List<double>(procCount);
                        for (int i = 0; i < procCount; i++)
                        {
                            long delta = counts[i] - _prevInterruptCount[i];
                            rates.Add(delta > 0 ? delta / elapsedSec : 0);
                        }
                    }
                }

                _prevInterruptCount = counts;
                _prevInterruptCountTime = now;
                return rates;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    public List<CoreDpcQueueRow> SampleQueueRates()
    {
        var rows = new List<CoreDpcQueueRow>();
        if (!_queueCountersReady) return rows;
        try
        {
            for (int i = 0; i < _queuedCounters.Count; i++)
            {
                rows.Add(new CoreDpcQueueRow
                {
                    CoreLabel = $"Core {_coreInstanceNames[i]}",
                    DpcsQueuedPerSec = _queuedCounters[i].NextValue(),
                    DpcRate = _rateCounters[i].NextValue(),
                });
            }
        }
        catch
        {
            // best-effort
        }
        return rows;
    }

    public void Dispose()
    {
        foreach (var c in _queuedCounters) c.Dispose();
        foreach (var c in _rateCounters) c.Dispose();
        foreach (var c in _interruptRateCounters) c.Dispose();
    }
}
