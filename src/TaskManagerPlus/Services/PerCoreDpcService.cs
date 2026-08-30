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

    // #1077: one whole-category CategorySampler read serves every per-core counter value - the
    // replacement for the old one-PerformanceCounter-per-(counter, core) lists, where every
    // NextValue() re-read the ENTIRE "Processor Information" category (up to ~72 full category
    // reads per 2s tick on a 24-thread machine; see CategorySampler's remarks). SampleQueueRates
    // and SampleInterruptStorm are called back-to-back from ResponsivenessViewModel's light tick,
    // so EnsureTicked coalesces them onto a single ReadCategory per cycle.
    private readonly CategorySampler _processorInfo = new("Processor Information");
    private readonly List<string> _coreInstanceNames = new();
    private bool _queueCountersReady;
    private bool _hasInterruptCounter;
    private long _lastCategoryTickMs = long.MinValue;

    /// <summary>Minimum spacing between whole-category reads - well under the 2s light-timer
    /// cadence, so the two sample methods called within the same timer tick share one read while
    /// consecutive timer ticks each get a fresh one.</summary>
    private const long CategoryTickCoalesceMs = 500;

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
    /// an empty list (the #206 tiles stay hidden) rather than throwing. The initial Tick() here is
    /// the priming read (CategorySampler's rate counters need a previous sample), replacing the old
    /// per-counter priming NextValue() loop - which itself cost one full category read per counter.</summary>
    private void TryInitQueueCounters()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists("Processor Information")) return;

            _processorInfo.Tick();
            _lastCategoryTickMs = Environment.TickCount64;

            var instances = _processorInfo.InstanceNames("DPCs Queued/sec")
                .Where(n => n.Contains(',') && !n.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            _hasInterruptCounter = _processorInfo.HasCounter("Interrupts/sec");
            _coreInstanceNames.AddRange(instances);
            _queueCountersReady = _coreInstanceNames.Count > 0;
        }
        catch
        {
            _queueCountersReady = false;
        }
    }

    /// <summary>#1077: reads the whole "Processor Information" category at most once per
    /// <see cref="CategoryTickCoalesceMs"/> window, so the sample methods called within one light-
    /// timer tick share a single ReadCategory (and each timer tick still gets fresh data). Uses
    /// Environment.TickCount64 (monotonic) rather than wall-clock so a clock step can't starve or
    /// double the reads.</summary>
    private void EnsureTicked()
    {
        long now = Environment.TickCount64;
        if (_lastCategoryTickMs != long.MinValue && now - _lastCategoryTickMs < CategoryTickCoalesceMs) return;
        _processorInfo.Tick();
        _lastCategoryTickMs = now;
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
        var rates = _queueCountersReady && _hasInterruptCounter ? SampleFromCounters() : SampleFromSyscall();
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
            EnsureTicked();
            var rates = new List<double>(_coreInstanceNames.Count);
            foreach (var inst in _coreInstanceNames) rates.Add(_processorInfo.Value("Interrupts/sec", inst));
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
            EnsureTicked();
            foreach (var inst in _coreInstanceNames)
            {
                rows.Add(new CoreDpcQueueRow
                {
                    CoreLabel = $"Core {inst}",
                    DpcsQueuedPerSec = _processorInfo.Value("DPCs Queued/sec", inst),
                    DpcRate = _processorInfo.Value("DPC Rate", inst),
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
        // #1077: nothing to dispose anymore - CategorySampler holds no PerformanceCounter objects,
        // only the previous tick's samples. Kept so the owning ViewModel's Dispose wiring stands.
    }
}
