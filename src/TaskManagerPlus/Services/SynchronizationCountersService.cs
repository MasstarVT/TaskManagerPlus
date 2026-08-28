using System.Diagnostics;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #273: the optional Windows "Synchronization" performance-counter category (Spinlock Acquires/sec,
/// Spinlock Contentions/sec, Exec. Resource Contentions/sec) - a kernel-contention view that isn't
/// enabled by default (it requires a boot-time kernel performance-counter flag most machines never
/// set). Existence-checked the same way PerCoreDpcService already guards its own optional
/// "Processor Information" queue counters (PerformanceCounterCategory.Exists), and degrades to
/// IsAvailable=false - the card this backs is hidden entirely, never shown with zeroed values, per
/// CLAUDE.md's "degrade to hidden, never fabricate" rule. This is the common case; seeing real
/// numbers here means the machine was deliberately configured for kernel-contention diagnostics.
///
/// A single, instance-less category (like "System"/"Memory"), so no per-instance enumeration is
/// needed - unlike PerCoreDpcService's per-core counters.
/// </summary>
public sealed class SynchronizationCountersService : IDisposable
{
    private const string Category = "Synchronization";

    private PerformanceCounter? _acquires;
    private PerformanceCounter? _contentions;
    private PerformanceCounter? _execResourceContentions;

    public bool IsAvailable { get; }

    public SynchronizationCountersService()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(Category))
            {
                IsAvailable = false;
                return;
            }

            _acquires = TryCreate("Spinlock Acquires/sec");
            _contentions = TryCreate("Spinlock Contentions/sec");
            _execResourceContentions = TryCreate("Exec. Resource Contentions/sec");
            IsAvailable = _acquires is not null || _contentions is not null || _execResourceContentions is not null;
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
            var counter = new PerformanceCounter(Category, counterName, readOnly: true);
            _ = counter.NextValue(); // prime it - a counter's first read is always 0/meaningless
            return counter;
        }
        catch
        {
            return null;
        }
    }

    public SynchronizationCountersInfo Sample()
    {
        if (!IsAvailable)
        {
            return new SynchronizationCountersInfo
            {
                IsAvailable = false,
                StatusText = "The optional \"Synchronization\" performance-counter category isn't enabled on this system - it requires a boot-time kernel flag most machines never set. This is the common case, not a failure.",
            };
        }

        try
        {
            return new SynchronizationCountersInfo
            {
                IsAvailable = true,
                SpinlockAcquiresPerSec = _acquires?.NextValue() ?? 0,
                SpinlockContentionsPerSec = _contentions?.NextValue() ?? 0,
                ExecResourceContentionsPerSec = _execResourceContentions?.NextValue() ?? 0,
            };
        }
        catch
        {
            return new SynchronizationCountersInfo { IsAvailable = false, StatusText = "Read failed - the counter(s) may have gone away." };
        }
    }

    public void Dispose()
    {
        _acquires?.Dispose();
        _contentions?.Dispose();
        _execResourceContentions?.Dispose();
    }
}
