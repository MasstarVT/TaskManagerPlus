using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TaskManagerPlus.Services;

/// <summary>The four fixed probe tiers item #503's matrix is built from - also exactly the set
/// item #501's continuous ring probes every cycle, so the matrix is just a live read of the same
/// rolling data the charts are built from, not a second probe stream.</summary>
public enum LatencyTier { Nic, Gateway, FirstHop, Resolver }

/// <summary>One completed probe against one tier.</summary>
public sealed record LatencyProbeResult(
    LatencyTier Tier,
    DateTime TimestampUtc,
    bool Success,
    double RoundtripMs,
    /// <summary>ICMP reply TTL (#508) - null when the probe failed outright or fell back to a
    /// TCP handshake (TCP's connect-time doesn't expose the remote's TTL without raw sockets).</summary>
    int? Ttl,
    /// <summary>#509: true when this reading came from timing a TCP connect rather than an ICMP
    /// echo - ICMP got nothing back but the host was otherwise reachable.</summary>
    bool UsedTcpFallback);

/// <summary>Min/avg/max/loss over the current rolling window for one tier - backs both the
/// #503 matrix table and the #504 baseline comparison.</summary>
public sealed record LatencyTierStats(
    LatencyTier Tier, string Label, string? TargetHost,
    double MinMs, double AvgMs, double MaxMs, double LossPercent, int SampleCount);

/// <summary>One tick of the #502 loss strip.</summary>
public sealed record LatencyLossTick(DateTime TimestampUtc, bool Success, bool PartOfBurst);

/// <summary>
/// Items 501-503/507-509 (suggestions.md "Continuous latency, jitter and packet-loss
/// monitoring"): an always-on probe ring, distinct from NetworkDiagnosticsService's existing
/// one-shot gateway/DNS check and on-demand RunJitterTestAsync test. Where those answer "is it
/// reachable right now", this exists to answer "did anything happen while I wasn't watching" -
/// so it keeps running (while started) and keeps a rolling window per target instead of a single
/// snapshot.
///
/// Probes four fixed tiers every cycle - the local NIC's own address (a near-zero-latency
/// control/sanity row), the default gateway, the first non-private hop toward the public
/// resolver, and the resolver itself - so a spike can be localized to "your Wi-Fi", "your ISP's
/// local plant", or "upstream/peering" (#503) rather than just "the internet is slow".
///
/// Every probe tries ICMP first and only falls back to timing a TCP connect (port 443, then 80)
/// when ICMP gets nothing back (#509) - fixes the documented blind spot where a firewalled-but-
/// reachable host reads as flatly "unreachable".
///
/// All public reads take a snapshot under a lock rather than exposing the live collections
/// directly - the probe loop runs on a background Task, so callers (NetworkViewModel, marshaling
/// onto the UI thread from CycleCompleted) must not touch the raw internal state.
/// </summary>
public sealed class LatencyMonitorService : IDisposable
{
    /// <summary>Rolling sample count kept per tier - backs the charts, matrix, loss strip and
    /// baseline updates all at once, so there's exactly one window per tier, not several.</summary>
    public const int WindowSize = 240;

    private const int TimeoutMs = 1000;
    private const string ResolverHost = "1.1.1.1";
    private static readonly TimeSpan TargetRefreshInterval = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan TtlHistoryWindow = TimeSpan.FromHours(1);
    private static readonly byte[] PingBuffer = System.Text.Encoding.ASCII.GetBytes("TaskManagerPlusLatencyProbe");

    private readonly object _lock = new();
    private readonly Dictionary<LatencyTier, Queue<LatencyProbeResult>> _windows = new();
    private readonly Dictionary<LatencyTier, List<(DateTime When, int Ttl)>> _ttlHistory = new();
    private readonly Dictionary<LatencyTier, string?> _targetHosts = new();

    private DateTime _lastTargetRefreshUtc = DateTime.MinValue;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    /// <summary>Fired on the background probe loop's own thread once per completed cycle (all
    /// four tiers) - NOT the UI thread. Subscribers must marshal back themselves, same as every
    /// other service/ViewModel boundary in this app that isn't already Task.Run-wrapped by the
    /// caller.</summary>
    public event Action? CycleCompleted;

    public LatencyMonitorService()
    {
        foreach (LatencyTier tier in Enum.GetValues<LatencyTier>())
        {
            _windows[tier] = new Queue<LatencyProbeResult>();
            _ttlHistory[tier] = new List<(DateTime, int)>();
            _targetHosts[tier] = null;
        }
    }

    public void Start(double intervalSeconds)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        var interval = TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 1.0, 30.0));
        _loopTask = Task.Run(() => RunLoopAsync(interval, _cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* already disposed */ }
    }

    public void Dispose()
    {
        Stop();
        try { _cts?.Dispose(); } catch { /* best-effort */ }
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RefreshTargetsIfDueAsync(token);

                var tiers = Enum.GetValues<LatencyTier>();
                var tasks = tiers.Select(t => ProbeAsync(t, token)).ToArray();
                var results = await Task.WhenAll(tasks);

                lock (_lock)
                {
                    foreach (var r in results) RecordSampleLocked(r);
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

    /// <summary>Target hosts change rarely (gateway/first-hop can shift on a route change, but
    /// not every couple seconds) - re-resolving them every probe cycle would mean the #501 ring
    /// itself does the expensive TTL-stepping traceroute walk on every tick. Refreshed every few
    /// minutes instead, and once eagerly on the very first cycle.</summary>
    private async Task RefreshTargetsIfDueAsync(CancellationToken token)
    {
        if (DateTime.UtcNow - _lastTargetRefreshUtc < TargetRefreshInterval && _targetHosts[LatencyTier.Gateway] is not null)
            return;

        _targetHosts[LatencyTier.Nic] = FindPrimaryLocalAddress();
        _targetHosts[LatencyTier.Gateway] = NetworkDiagnosticsService.FindDefaultGateway();
        _targetHosts[LatencyTier.Resolver] = ResolverHost;
        _targetHosts[LatencyTier.FirstHop] = await DiscoverFirstPublicHopAsync(ResolverHost, token);

        _lastTargetRefreshUtc = DateTime.UtcNow;
    }

    private async Task<LatencyProbeResult> ProbeAsync(LatencyTier tier, CancellationToken token)
    {
        string? host = _targetHosts.TryGetValue(tier, out var h) ? h : null;
        var now = DateTime.UtcNow;
        if (string.IsNullOrEmpty(host))
            return new LatencyProbeResult(tier, now, false, 0, null, false);

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, TimeoutMs, PingBuffer);
            if (reply.Status == IPStatus.Success)
            {
                int? ttl = null;
                try { ttl = reply.Options?.Ttl; } catch { /* some transports don't return Options */ }
                return new LatencyProbeResult(tier, now, true, reply.RoundtripTime, ttl, false);
            }
        }
        catch
        {
            // Fall through to the TCP fallback below - #509.
        }

        var (tcpOk, tcpMs) = await ProbeTcpAsync(host, token);
        return new LatencyProbeResult(tier, now, tcpOk, tcpMs, null, true);
    }

    /// <summary>#509: ICMP got nothing back - time a TCP handshake instead of reporting flatly
    /// "unreachable", since a firewalled-but-otherwise-fine host is a documented blind spot of a
    /// pure-ping check. Tries 443 then 80; the resulting figure is a real "how long did the
    /// handshake take" latency, just via a different transport than an ICMP echo.</summary>
    private static async Task<(bool Success, double Ms)> ProbeTcpAsync(string host, CancellationToken token)
    {
        foreach (int port in new[] { 443, 80 })
        {
            try
            {
                using var client = new TcpClient();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var connectTask = client.ConnectAsync(host, port);
                var winner = await Task.WhenAny(connectTask, Task.Delay(TimeoutMs, token));
                if (winner == connectTask && client.Connected)
                {
                    sw.Stop();
                    return (true, sw.Elapsed.TotalMilliseconds);
                }
            }
            catch
            {
                // try the next port
            }
        }
        return (false, 0);
    }

    private void RecordSampleLocked(LatencyProbeResult r)
    {
        var q = _windows[r.Tier];
        q.Enqueue(r);
        while (q.Count > WindowSize) q.Dequeue();

        // #508: only successful ICMP replies carry a TTL - a TCP-fallback or failed probe simply
        // doesn't add a data point rather than recording a false "no change".
        if (r.Success && r.Ttl is { } ttl)
        {
            var hist = _ttlHistory[r.Tier];
            hist.Add((r.TimestampUtc, ttl));
            var cutoff = DateTime.UtcNow - TtlHistoryWindow;
            hist.RemoveAll(e => e.When < cutoff);
        }
    }

    public string? GetTargetHost(LatencyTier tier)
    {
        lock (_lock) { return _targetHosts.TryGetValue(tier, out var h) ? h : null; }
    }

    public bool TryGetLatest(LatencyTier tier, out LatencyProbeResult result)
    {
        lock (_lock)
        {
            var arr = _windows[tier].ToArray();
            if (arr.Length == 0) { result = default!; return false; }
            result = arr[^1];
            return true;
        }
    }

    public LatencyTierStats GetStats(LatencyTier tier)
    {
        lock (_lock)
        {
            var samples = _windows[tier].ToArray();
            string? host = _targetHosts.TryGetValue(tier, out var h) ? h : null;
            string label = LabelOf(tier);
            if (samples.Length == 0)
                return new LatencyTierStats(tier, label, host, 0, 0, 0, 0, 0);

            var ok = samples.Where(s => s.Success).ToArray();
            double loss = (samples.Length - ok.Length) * 100.0 / samples.Length;
            if (ok.Length == 0)
                return new LatencyTierStats(tier, label, host, 0, 0, 0, loss, samples.Length);

            return new LatencyTierStats(tier, label, host,
                ok.Min(s => s.RoundtripMs), ok.Average(s => s.RoundtripMs), ok.Max(s => s.RoundtripMs),
                loss, samples.Length);
        }
    }

    /// <summary>Successful round-trip readings in the current window, oldest-first - the raw
    /// input #504's baseline update and any future jitter/percentile computation needs.</summary>
    public List<double> GetSuccessfulRoundtrips(LatencyTier tier)
    {
        lock (_lock) { return _windows[tier].Where(s => s.Success).Select(s => s.RoundtripMs).ToList(); }
    }

    /// <summary>#502: the most recent <paramref name="count"/> probes as loss-strip ticks,
    /// oldest-first, each flagged whether it's part of a run of 2+ consecutive losses (a burst -
    /// points at a link/radio problem) versus an isolated single drop (more often congestion or a
    /// rate-limited ICMP responder). Quick flag, not a verdict - see this class's remarks.</summary>
    public List<LatencyLossTick> GetLossStrip(LatencyTier tier, int count = 60)
    {
        lock (_lock)
        {
            var samples = _windows[tier].ToArray();
            var recent = samples.Length > count ? samples[^count..] : samples;

            var ticks = new List<LatencyLossTick>(recent.Length);
            for (int i = 0; i < recent.Length; i++)
            {
                bool success = recent[i].Success;
                bool partOfBurst = !success && ((i > 0 && !recent[i - 1].Success) || (i < recent.Length - 1 && !recent[i + 1].Success));
                ticks.Add(new LatencyLossTick(recent[i].TimestampUtc, success, partOfBurst));
            }
            return ticks;
        }
    }

    /// <summary>#502: isolated single-packet drops vs. multi-packet burst drops in the current
    /// window, for the loss card's summary caption.</summary>
    public (int IsolatedLosses, int BurstSamples, int BurstCount) GetLossBreakdown(LatencyTier tier)
    {
        lock (_lock)
        {
            var samples = _windows[tier].ToArray();
            int isolated = 0, burstSamples = 0, burstCount = 0;
            int i = 0;
            while (i < samples.Length)
            {
                if (samples[i].Success) { i++; continue; }
                int runStart = i;
                while (i < samples.Length && !samples[i].Success) i++;
                int runLength = i - runStart;
                if (runLength == 1) isolated++;
                else { burstSamples += runLength; burstCount++; }
            }
            return (isolated, burstSamples, burstCount);
        }
    }

    /// <summary>#508: how many times the reply TTL changed within the last hour for one tier - a
    /// changing TTL to a fixed target means the path length changed, i.e. the route flapped or
    /// failed over. Cheap: piggybacks on TTLs already captured by the #501 probes, no extra
    /// pings.</summary>
    public int GetTtlChangeCountLastHour(LatencyTier tier)
    {
        lock (_lock)
        {
            var hist = _ttlHistory[tier];
            int changes = 0;
            for (int i = 1; i < hist.Count; i++)
                if (hist[i].Ttl != hist[i - 1].Ttl) changes++;
            return changes;
        }
    }

    private static string LabelOf(LatencyTier tier) => tier switch
    {
        LatencyTier.Nic => "Local NIC",
        LatencyTier.Gateway => "Gateway",
        LatencyTier.FirstHop => "First hop",
        LatencyTier.Resolver => "Public resolver",
        _ => tier.ToString(),
    };

    /// <summary>The active internet-facing adapter's own IPv4 address (#503's "loopback-adjacent
    /// NIC" row) - the same adapter-selection rule (must have a real default gateway) as
    /// NetworkDiagnosticsService.FindDefaultGateway, so the NIC row and Gateway row in the matrix
    /// refer to the same physical link. Pinging your own address exercises the local IP
    /// stack/ARP without leaving the machine, so it's expected to read near-zero and near-perfect
    /// - a control row the other three are read against.</summary>
    private static string? FindPrimaryLocalAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var props = ni.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)) continue;

                var addr = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (addr is not null) return addr.ToString();
            }
        }
        catch
        {
            // Best-effort.
        }
        return null;
    }

    /// <summary>#501/#503: the first hop outside the local private network on the way to
    /// <paramref name="destination"/>, found by stepping the ICMP TTL up from 1 - the same
    /// technique tracert.exe itself uses internally - rather than shelling out to it on a timer.
    /// TracerouteService's existing tracert.exe wrapper stays reserved for the on-demand, full,
    /// user-triggered traceroute (#49); this needs to run unattended every few minutes forever, so
    /// a lightweight in-process walk (capped at 10 hops) is the better fit here. "First
    /// non-private" is a plain RFC1918/loopback/link-local/CGNAT address check, not a claim the
    /// hop is definitely the ISP's edge router.</summary>
    private static async Task<string?> DiscoverFirstPublicHopAsync(string destination, CancellationToken token)
    {
        try
        {
            using var ping = new Ping();
            for (int ttl = 1; ttl <= 10 && !token.IsCancellationRequested; ttl++)
            {
                var options = new PingOptions(ttl, true);
                PingReply reply;
                try { reply = await ping.SendPingAsync(destination, TimeoutMs, PingBuffer, options); }
                catch { continue; }

                if (reply.Status is not (IPStatus.TtlExpired or IPStatus.Success) || reply.Address is null)
                    continue;

                if (!IsPrivateOrLocal(reply.Address)) return reply.Address.ToString();
                if (reply.Status == IPStatus.Success) break; // reached the destination within an all-private hop count
            }
        }
        catch
        {
            // Best-effort - the FirstHop tier just reports "no data" until this succeeds.
        }
        return null;
    }

    private static bool IsPrivateOrLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork) return true; // only walking IPv4 hops here
        byte[] b = address.GetAddressBytes();
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 169 && b[1] == 254) return true;
        if (b[0] == 100 && b[1] is >= 64 and <= 127) return true; // CGNAT (RFC 6598)
        return false;
    }
}
