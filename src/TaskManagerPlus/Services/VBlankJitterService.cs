using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #249: a dedicated background thread loops on DwmFlush() (dwmapi.dll, blocks the calling thread
/// until the next vblank) and records the QPC interval between returns - a healthy system shows a
/// tight distribution around the refresh period, compositor stalls show up as long tails. Per
/// CLAUDE.md's "prefer a known tool" rule, DwmFlush has no tool/WMI equivalent, so raw P/Invoke is
/// the documented exception here (same tier as DwmCompositionService).
///
/// Must be Start/Stop-gated (a thread looping forever on a blocking native call is not something
/// to run by default) - uses a plain background Thread rather than Task.Run, since this loop blocks
/// on native code for its entire lifetime rather than yielding to the thread pool; a
/// CancellationTokenSource gates the loop between DwmFlush calls the same way the DPC measurement
/// session's own loop is gated, but DwmFlush itself can't be interrupted mid-call - Stop has bounded
/// latency (at most one more vblank period) rather than being instant, which is an acceptable
/// tradeoff for a diagnostic probe.
/// </summary>
public sealed class VBlankJitterService : IDisposable
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    // Implausible spans (system sleep/resume, a stalled DWM taking seconds) are dropped rather than
    // skewing the percentiles - a jitter probe cares about the normal-to-bad range, not outliers
    // caused by something else entirely.
    private const double MaxPlausibleMs = 5000;
    private const int MaxSamples = 2000;

    private readonly object _lock = new();
    private readonly List<double> _intervalsMs = new();

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private volatile bool _isRunning;
    private volatile string _statusText = "Not running - press Start.";

    public bool IsRunning => _isRunning;

    public void Start()
    {
        if (_isRunning) return;
        lock (_lock) _intervalsMs.Clear();
        _cts = new CancellationTokenSource();
        _isRunning = true;
        _statusText = "Measuring vblank intervals...";
        _thread = new Thread(() => RunLoop(_cts.Token)) { IsBackground = true, Name = "VBlankJitterProbe" };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _cts?.Cancel();
        _isRunning = false;
        _statusText = "Stopped.";
    }

    private void RunLoop(CancellationToken ct)
    {
        try
        {
            long lastQpc = 0;
            double freq = TimerResolutionService.QpcFrequency;
            if (freq <= 0)
            {
                _statusText = "Unknown - QueryPerformanceFrequency failed, can't time vblank intervals.";
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                int hr = DwmFlush();
                QueryPerformanceCounter(out long now);
                if (hr != 0)
                {
                    _statusText = $"DwmFlush failed (0x{hr:X8}) - composition may be disabled, or this is a remote-desktop session.";
                    break;
                }

                if (lastQpc != 0)
                {
                    double ms = (now - lastQpc) / freq * 1000.0;
                    if (ms is > 0 and < MaxPlausibleMs)
                    {
                        lock (_lock)
                        {
                            _intervalsMs.Add(ms);
                            if (_intervalsMs.Count > MaxSamples) _intervalsMs.RemoveAt(0);
                        }
                    }
                }
                lastQpc = now;
            }
        }
        catch (Exception ex)
        {
            _statusText = $"Probe failed: {ex.Message}";
        }
        finally
        {
            _isRunning = false;
        }
    }

    public VBlankJitterSnapshot GetSnapshot()
    {
        List<double> copy;
        lock (_lock) copy = new List<double>(_intervalsMs);

        if (copy.Count == 0)
            return new VBlankJitterSnapshot { StatusText = _statusText };

        copy.Sort();
        double P(double p) => copy[Math.Clamp((int)Math.Ceiling(p * copy.Count) - 1, 0, copy.Count - 1)];

        return new VBlankJitterSnapshot
        {
            SampleCount = copy.Count,
            P50Ms = P(0.50),
            P99Ms = P(0.99),
            MaxMs = copy[^1],
            StatusText = $"{copy.Count} vblank interval(s) captured. {_statusText}",
        };
    }

    public void Dispose() => Stop();
}
