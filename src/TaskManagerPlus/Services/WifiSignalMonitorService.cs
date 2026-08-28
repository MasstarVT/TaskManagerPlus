namespace TaskManagerPlus.Services;

/// <summary>One completed WLAN native-API sample (#537).</summary>
public sealed record WifiSignalSample(
    DateTime TimestampUtc, int? RssiDbm, double? RxRateMbps, double? TxRateMbps, string? Bssid,
    /// <summary>True when this sample's BSSID differs from the previous successful sample's - a
    /// roam within the same SSID (or a fresh association after a drop). Drawn as a vertical marker
    /// on the #537 chart so a mid-session dropout can be matched to a roam.</summary>
    bool RoamedFromPrevious);

/// <summary>
/// Item #537: continuous RSSI/link-rate sampling, distinct from the on-demand neighbour scan
/// WifiChannelScanService performs for #538. Same "always-on background loop with a start/stop
/// toggle" shape LatencyMonitorService (#501) already establishes for this tab - a
/// CancellationTokenSource-driven Task.Run loop, a lock-guarded rolling window, and a
/// CycleCompleted event fired on the loop's own thread that callers must marshal to the UI thread
/// themselves.
///
/// Unlike the #501 latency ring, this doesn't get a user-facing Start/Stop button: it auto-starts
/// whenever the Network tab observes a live Wi-Fi association (see NetworkViewModel's connectivity
/// tick) and stops when that association goes away, because WlanNativeService's underlying reads
/// are cheap local API calls - the same "trivial enough to just poll" tier PerformanceCounter reads
/// occupy, not the "gate behind a button" tier CLAUDE.md reserves for event-log scans, filesystem
/// walks, or anything that disturbs the radio (unlike WifiChannelScanService's scan, this never
/// triggers one).
/// </summary>
public sealed class WifiSignalMonitorService : IDisposable
{
    /// <summary>~4 minutes of history at the default 2s interval - enough to see a roam-to-dropout
    /// correlation without the window growing unbounded.</summary>
    public const int WindowSize = 120;

    private readonly WlanNativeService _wlan;
    private readonly object _lock = new();
    private readonly Queue<WifiSignalSample> _window = new();
    private string? _lastBssid;
    private WifiRadioSnapshot? _latestSnapshot;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    /// <summary>Fired on this service's own background-loop thread, not the UI thread - callers
    /// must marshal back themselves, same contract as LatencyMonitorService.CycleCompleted.</summary>
    public event Action? CycleCompleted;

    public WifiSignalMonitorService(WlanNativeService wlan)
    {
        _wlan = wlan;
    }

    public void Start(double intervalSeconds = 2.0)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        var interval = TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 1.0, 10.0));
        _loopTask = Task.Run(() => RunLoopAsync(interval, _cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* already disposed */ }
        lock (_lock)
        {
            _window.Clear();
            _lastBssid = null;
            _latestSnapshot = null;
        }
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var snap = _wlan.GetSnapshot();
                var now = DateTime.UtcNow;
                bool roamed = snap is not null && _lastBssid is not null
                    && !string.Equals(_lastBssid, snap.Bssid, StringComparison.OrdinalIgnoreCase);
                if (snap is not null) _lastBssid = snap.Bssid;

                lock (_lock)
                {
                    _window.Enqueue(new WifiSignalSample(now, snap?.RssiDbm, snap?.RxRateMbps, snap?.TxRateMbps, snap?.Bssid, roamed));
                    while (_window.Count > WindowSize) _window.Dequeue();
                    _latestSnapshot = snap;
                }

                CycleCompleted?.Invoke();
            }
            catch
            {
                // Best-effort - one bad cycle shouldn't kill the loop for the rest of the session.
            }

            try { await Task.Delay(interval, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Oldest-first snapshot of the current rolling window, taken under a lock rather than
    /// exposing the live queue directly - the loop runs on a background Task.</summary>
    public List<WifiSignalSample> GetWindow()
    {
        lock (_lock) { return _window.ToList(); }
    }

    /// <summary>#536/#540/#545: the full radio snapshot (SSID/BSSID/PHY type/channel/link quality)
    /// from the most recent cycle, not just the trimmed RSSI/rate figures the rolling window keeps -
    /// the Wi-Fi card's headline readout rides this same 2s cadence rather than a second native
    /// call.</summary>
    public WifiRadioSnapshot? GetLatestSnapshot()
    {
        lock (_lock) { return _latestSnapshot; }
    }

    public void Dispose()
    {
        Stop();
        try { _cts?.Dispose(); } catch { /* best-effort */ }
    }
}
