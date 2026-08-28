using System.Net.NetworkInformation;

namespace TaskManagerPlus.Services;

/// <summary>One hop's accumulated sent/received/loss/min/avg/max stats (#515) - a point-in-time
/// snapshot handed out by <see cref="MtrService.GetSnapshot"/>, distinct from the mutable internal
/// tracker the probe loop accumulates into under lock.</summary>
public sealed record MtrHopStats(int Hop, string? Address, int Sent, int Received, double LossPercent, double MinMs, double AvgMs, double MaxMs, double LastMs);

/// <summary>
/// Item #515 (suggestions.md "Path MTU, routing and hop-level diagnostics"): an MTR-style
/// continuous hop monitor - repeatedly probes every hop of a path with increasing-TTL pings and
/// accumulates a per-hop sent/received/loss/min/avg/max table, refreshing in place. Distinct from
/// TracerouteService's existing one-shot `tracert -d` text dump: that answers "what's the path
/// right now", this answers "which specific hop is dropping packets over time" - a one-shot
/// traceroute that happens to catch a hop mid-timeout looks identical to one that's chronically
/// lossy; only a running window tells them apart.
///
/// Built directly on <see cref="Ping"/> with <see cref="PingOptions.Ttl"/> (rather than shelling
/// out to tracert.exe repeatedly, which reprints a whole fresh trace every call instead of
/// accumulating per-hop history) - the one item in this batch that P/Invokes/uses raw framework
/// APIs instead of a Windows tool, since there's no command-line tool that keeps a running
/// per-hop average the way this needs. Own explicit start/stop toggle (#515's own requirement),
/// not the shared 15s connectivity timer or the #501 latency ring - a continuous probe sweep
/// across every hop is a meaningfully heavier, opt-in workload.
///
/// Same "run the probe loop on a background Task, expose only locked snapshots" shape
/// LatencyMonitorService already establishes for this tab's other continuous prober - see that
/// class's remarks for the rationale.
/// </summary>
public sealed class MtrService : IDisposable
{
    public const int MaxHops = 30;
    private const int TimeoutMs = 1500;

    private sealed class HopTracker
    {
        public int Hop;
        public string? Address;
        public int Sent;
        public int Received;
        public double MinMs = double.MaxValue;
        public double MaxMs;
        public double SumMs;
        public double LastMs;
    }

    private readonly object _lock = new();
    private readonly Dictionary<int, HopTracker> _hops = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    // Once a cycle's probe at some TTL comes back as the destination itself (IPStatus.Success),
    // there's no point probing further hops on later cycles either - this narrows to that count.
    private volatile int _knownHopCount = MaxHops;

    public event Action? CycleCompleted;
    public bool IsRunning { get; private set; }
    public string? Target { get; private set; }

    public void Start(string host, double intervalSeconds)
    {
        Stop();
        Target = host;
        lock (_lock) _hops.Clear();
        _knownHopCount = MaxHops;

        _cts = new CancellationTokenSource();
        IsRunning = true;
        _loopTask = Task.Run(() => RunLoopAsync(host, intervalSeconds, _cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning && _cts is null) return;
        try { _cts?.Cancel(); } catch { /* best-effort */ }
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort - don't block the UI thread indefinitely on a stuck probe */ }
        _cts?.Dispose();
        _cts = null;
        IsRunning = false;
    }

    private async Task RunLoopAsync(string host, double intervalSeconds, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOneCycleAsync(host, ct);
            }
            catch
            {
                // Best-effort - one bad cycle (e.g. transient resolution failure) shouldn't kill the loop.
            }

            if (ct.IsCancellationRequested) break;
            CycleCompleted?.Invoke();

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.5, intervalSeconds)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOneCycleAsync(string host, CancellationToken ct)
    {
        int maxTtl = _knownHopCount;
        for (int ttl = 1; ttl <= maxTtl && !ct.IsCancellationRequested; ttl++)
        {
            PingReply? reply = null;
            try
            {
                using var ping = new Ping();
                var options = new PingOptions { Ttl = ttl, DontFragment = false };
                var buffer = new byte[32];
                reply = await ping.SendPingAsync(host, TimeoutMs, buffer, options);
            }
            catch
            {
                // Leave reply null - treated as a timed-out probe for this hop this cycle.
            }

            lock (_lock)
            {
                if (!_hops.TryGetValue(ttl, out var tracker))
                {
                    tracker = new HopTracker { Hop = ttl };
                    _hops[ttl] = tracker;
                }

                tracker.Sent++;
                bool responded = reply is not null && reply.Status is IPStatus.TtlExpired or IPStatus.Success;
                if (responded)
                {
                    tracker.Received++;
                    if (reply!.Address is not null) tracker.Address = reply.Address.ToString();
                    double rtt = reply.RoundtripTime;
                    tracker.MinMs = Math.Min(tracker.MinMs, rtt);
                    tracker.MaxMs = Math.Max(tracker.MaxMs, rtt);
                    tracker.SumMs += rtt;
                    tracker.LastMs = rtt;
                }

                if (reply?.Status == IPStatus.Success)
                    _knownHopCount = Math.Min(_knownHopCount, ttl);
            }
        }
    }

    /// <summary>Locked snapshot of every hop probed so far, ordered by hop number - safe to call
    /// from any thread; the probe loop itself runs on its own background Task.</summary>
    public List<MtrHopStats> GetSnapshot()
    {
        lock (_lock)
        {
            return _hops.Values
                .OrderBy(h => h.Hop)
                .Select(h => new MtrHopStats(
                    h.Hop, h.Address, h.Sent, h.Received,
                    h.Sent == 0 ? 0 : (h.Sent - h.Received) * 100.0 / h.Sent,
                    h.Received == 0 ? 0 : h.MinMs,
                    h.Received == 0 ? 0 : h.SumMs / h.Received,
                    h.Received == 0 ? 0 : h.MaxMs,
                    h.LastMs))
                .ToList();
        }
    }

    public void Dispose() => Stop();
}
