using System.Diagnostics;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #273: the Windows "Synchronization" performance-counter category (Spinlock Acquires/sec,
/// Spinlock Contentions/sec, Exec. Resource Total Contentions/sec) - a kernel-contention view.
/// The category is multi-instance (one instance per logical processor plus "_Total"), so values
/// are read from the "_Total" instance; a PerformanceCounter created without an instance throws
/// on its first read, which is what used to make this card look unavailable everywhere. Read
/// through CategorySampler (one category read per tick, not one per counter). Degrades to
/// IsAvailable=false - the card this backs is hidden entirely, never shown with zeroed values, per
/// CLAUDE.md's "degrade to hidden, never fabricate" rule.
/// </summary>
public sealed class SynchronizationCountersService : IDisposable
{
    private const string Category = "Synchronization";
    private const string TotalInstance = "_Total";
    private const string AcquiresCounter = "Spinlock Acquires/sec";
    private const string ContentionsCounter = "Spinlock Contentions/sec";

    // Current Windows 10/11 builds name it "Exec. Resource Total Contentions/sec"; the shorter
    // name is kept as a fallback in case an older build used it.
    private static readonly string[] ExecResourceContentionNames = { "Exec. Resource Total Contentions/sec", "Exec. Resource Contentions/sec" };

    private readonly CategorySampler? _sampler;
    private readonly bool _hasAcquires;
    private readonly bool _hasContentions;
    private readonly string? _execResourceCounter;

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

            _sampler = new CategorySampler(Category);
            _sampler.Tick(); // prime - a rate counter's first read is otherwise 0

            _hasAcquires = _sampler.HasCounter(AcquiresCounter);
            _hasContentions = _sampler.HasCounter(ContentionsCounter);
            _execResourceCounter = ExecResourceContentionNames.FirstOrDefault(_sampler.HasCounter);
            IsAvailable = _hasAcquires || _hasContentions || _execResourceCounter is not null;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    public SynchronizationCountersInfo Sample()
    {
        if (!IsAvailable || _sampler is null)
        {
            return new SynchronizationCountersInfo
            {
                IsAvailable = false,
                StatusText = "The \"Synchronization\" performance-counter category couldn't be read on this system.",
            };
        }

        try
        {
            _sampler.Tick();
            return new SynchronizationCountersInfo
            {
                IsAvailable = true,
                SpinlockAcquiresPerSec = _hasAcquires ? _sampler.Value(AcquiresCounter, TotalInstance) : 0,
                SpinlockContentionsPerSec = _hasContentions ? _sampler.Value(ContentionsCounter, TotalInstance) : 0,
                ExecResourceContentionsPerSec = _execResourceCounter is { } exec ? _sampler.Value(exec, TotalInstance) : 0,
            };
        }
        catch
        {
            return new SynchronizationCountersInfo { IsAvailable = false, StatusText = "Read failed - the counter(s) may have gone away." };
        }
    }

    // CategorySampler holds no OS handles; kept IDisposable so the owning ViewModel's Dispose
    // call site is unchanged.
    public void Dispose()
    {
    }
}
