using System.Globalization;
using System.Text;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #296: a fixed-size (60s at 10Hz = up to 600 samples) in-memory ring buffer of already-computed
/// responsiveness data - "always running once armed, costs nothing to keep" per the item's own
/// framing, since every field of FlightRecorderSample is a value some earlier chunk in this domain
/// already sampled this tick, not a new syscall/perf-counter read. Deliberately dumb: Snapshot()
/// appends and trims, GetLast()/GetAll() read back a window - the substrate #297 (trigger rules),
/// #298 (ETW capture) and #299/#300 (export/replay) are all built on top of, per the item's own
/// "build it as a clean, reusable service" instruction.
///
/// Thread-safety: Snapshot() is only ever called from the owning DispatcherTimer's tick (the UI
/// thread), but GetLast()/GetAll()/ToCsv() may be called from a background Task (e.g. #299's
/// export) while a tick is in flight - a simple lock keeps the two from racing on the underlying
/// list, the same "cheap enough not to matter" tier as HungWindowService's own internal locking.
/// </summary>
public sealed class FlightRecorderService
{
    public const int SampleHz = 10;
    public static readonly TimeSpan MaxWindow = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private readonly List<FlightRecorderSample> _ring = new((int)MaxWindow.TotalSeconds * SampleHz + 8);

    public int Count { get { lock (_lock) return _ring.Count; } }

    /// <summary>Oldest/newest sample currently buffered, or null if empty - used for the "covering
    /// the last N seconds" status text and to gate #297/#300 actions that need at least one sample.</summary>
    public (DateTime? Oldest, DateTime? Newest) Span
    {
        get
        {
            lock (_lock)
                return _ring.Count == 0 ? (null, null) : (_ring[0].TimestampUtc, _ring[^1].TimestampUtc);
        }
    }

    public void Snapshot(FlightRecorderSample sample)
    {
        lock (_lock)
        {
            _ring.Add(sample);
            var cutoff = sample.TimestampUtc - MaxWindow;
            int removeCount = 0;
            while (removeCount < _ring.Count && _ring[removeCount].TimestampUtc < cutoff) removeCount++;
            if (removeCount > 0) _ring.RemoveRange(0, removeCount);
        }
    }

    /// <summary>The most recent <paramref name="span"/> of buffered samples, oldest first - used
    /// for #299's incident export (the last 60s) and could be called with a shorter window too.</summary>
    public List<FlightRecorderSample> GetLast(TimeSpan span)
    {
        lock (_lock)
        {
            if (_ring.Count == 0) return new List<FlightRecorderSample>();
            var cutoff = _ring[^1].TimestampUtc - span;
            return _ring.Where(s => s.TimestampUtc >= cutoff).ToList();
        }
    }

    public List<FlightRecorderSample> GetAll()
    {
        lock (_lock) return new List<FlightRecorderSample>(_ring);
    }

    public void Clear() { lock (_lock) _ring.Clear(); }

    /// <summary>#296/#299: the ring-buffer CSV export - the same quote-escaping convention
    /// LoggingViewModel's own CSV writer uses (wrap in quotes, double an embedded quote), so a file
    /// this writes and LogReplayService.ParseFlightRecorderCsv reads round-trip cleanly, and so it
    /// opens cleanly in Excel like every other CSV this app produces.</summary>
    public static string ToCsv(IReadOnlyList<FlightRecorderSample> samples)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Headers));
        foreach (var s in samples)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Escape(s.TimestampUtc.ToString("o", CultureInfo.InvariantCulture)),
                Escape(s.CpuPercent.ToString("0.##", CultureInfo.InvariantCulture)),
                Escape(s.MaxCoreDpcPercent.ToString("0.##", CultureInfo.InvariantCulture)),
                Escape(s.ProcessorQueueLength.ToString("0.##", CultureInfo.InvariantCulture)),
                Escape(s.HardFaultsPerSec.ToString("0.##", CultureInfo.InvariantCulture)),
                Escape(s.FrameTimeMs?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty),
                Escape(s.InputDelayMs?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty),
                Escape(s.ForegroundProcessName),
                Escape(s.ForegroundWindowTitle),
                Escape(s.TopProcessesText),
            }));
        }
        return sb.ToString();
    }

    public static readonly string[] Headers =
    {
        "Timestamp", "CPU (%)", "Max core DPC (%)", "Processor queue length", "Hard faults/sec",
        "Frame time (ms)", "Input delay (ms)", "Foreground process", "Foreground window title", "Top processes",
    };

    private static string Escape(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
