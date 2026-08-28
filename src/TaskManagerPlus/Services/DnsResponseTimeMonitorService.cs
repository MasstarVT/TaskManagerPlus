using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace TaskManagerPlus.Services;

/// <summary>One completed DNS response-time probe against one resolver (#526).</summary>
public sealed record DnsResponseTimeSample(string ResolverIp, DateTime TimestampUtc, bool Success, double Ms);

/// <summary>
/// Item #526: charts resolution time for a fixed control hostname against each configured
/// resolver over time, sharing the #501 Latency card's own start/stop toggle rather than getting a
/// separate one - see NetworkViewModel.ToggleLatencyMonitor's remarks for the wiring.
///
/// Unlike DnsResolverService's #517/#519 one-shot comparison (which shells out to nslookup.exe -
/// fine for a single user-triggered run), this probes every configured resolver every few seconds,
/// indefinitely, for as long as the Latency monitor is running - repeatedly spawning a whole
/// nslookup.exe process per resolver per tick would be real, avoidable overhead for a background
/// prober, so this instead builds and sends a minimal raw UDP/53 query by hand and times the
/// reply, the same "raw socket is meaningfully simpler for a continuous prober" tradeoff
/// MtrService's remarks describe for choosing raw ICMP over repeated tracert.exe shell-outs. Only
/// measures whether *a* reply came back in time (matching the query's transaction ID) - it doesn't
/// parse or compare answer data, since #517's on-demand comparison already owns that job.
///
/// Same "run the probe loop on a background Task, expose only locked snapshots" shape
/// LatencyMonitorService/MtrService already establish for this tab's other continuous probers.
/// </summary>
public sealed class DnsResponseTimeMonitorService : IDisposable
{
    public const int WindowSize = 120;
    private const int TimeoutMs = 1500;

    // Windows' own connectivity-check hostname (used by NCSI) - the same neutral, always-resolvable
    // control hostname NetworkDiagnosticsService.TimeDnsLookupAsync already uses for its own
    // single-shot DNS-lookup timing, reused here so #526's chart and the Connectivity card's
    // existing DNS-resolution-time figure are measuring the same target.
    private const string ControlHostname = "www.msftconnecttest.com";

    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<DnsResponseTimeSample>> _windows = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public bool IsRunning => _loopTask is { IsCompleted: false };
    public event Action? CycleCompleted;

    public void Start(IReadOnlyList<string> resolvers, double intervalSeconds)
    {
        if (IsRunning) return;
        lock (_lock)
        {
            _windows.Clear();
            foreach (var r in resolvers) _windows[r] = new Queue<DnsResponseTimeSample>();
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 2.0, 30.0));
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(resolvers.ToList(), interval, _cts.Token));
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

    private async Task RunLoopAsync(List<string> resolvers, TimeSpan interval, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var tasks = resolvers.Select(ip => ProbeAsync(ip, token)).ToArray();
                var results = await Task.WhenAll(tasks);
                lock (_lock)
                {
                    foreach (var sample in results) RecordLocked(sample);
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

    private static async Task<DnsResponseTimeSample> ProbeAsync(string resolverIp, CancellationToken outerToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            timeoutCts.CancelAfter(TimeoutMs);

            using var udp = new UdpClient();
            udp.Connect(resolverIp, 53);

            byte[] query = BuildQuery(ControlHostname);
            await udp.SendAsync(query, timeoutCts.Token);
            var result = await udp.ReceiveAsync(timeoutCts.Token);
            sw.Stop();

            // A real DNS reply echoes the query's own 2-byte transaction ID back at the start of
            // the response - good enough confirmation this is a genuine answer to our own query
            // (not stray traffic on the same ephemeral port) without parsing the rest of the packet.
            bool ok = result.Buffer.Length > 2 && result.Buffer[0] == query[0] && result.Buffer[1] == query[1];
            return new DnsResponseTimeSample(resolverIp, DateTime.UtcNow, ok, sw.Elapsed.TotalMilliseconds);
        }
        catch
        {
            // Timed out, port unreachable, resolver dropped the packet outright - all read as a
            // failed probe rather than propagating into the loop.
            return new DnsResponseTimeSample(resolverIp, DateTime.UtcNow, false, 0);
        }
    }

    /// <summary>Hand-builds a minimal, standards-shaped DNS query packet (12-byte header + one
    /// QNAME/QTYPE=A/QCLASS=IN question) - the smallest packet that gets a real reply from a real
    /// resolver, since this probe only cares about round-trip timing, not the returned records.</summary>
    private static byte[] BuildQuery(string hostname)
    {
        ushort id = (ushort)Random.Shared.Next(ushort.MinValue, ushort.MaxValue + 1);
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(id >> 8));
        ms.WriteByte((byte)id);
        ms.Write(new byte[] { 0x01, 0x00 }); // flags: standard query, recursion desired
        ms.Write(new byte[] { 0x00, 0x01 }); // QDCOUNT = 1
        ms.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }); // ANCOUNT/NSCOUNT/ARCOUNT = 0

        foreach (var label in hostname.Split('.'))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0); // root label

        ms.Write(new byte[] { 0x00, 0x01 }); // QTYPE = A
        ms.Write(new byte[] { 0x00, 0x01 }); // QCLASS = IN
        return ms.ToArray();
    }

    private void RecordLocked(DnsResponseTimeSample sample)
    {
        if (!_windows.TryGetValue(sample.ResolverIp, out var q)) return;
        q.Enqueue(sample);
        while (q.Count > WindowSize) q.Dequeue();
    }

    public bool TryGetLatest(string resolverIp, out DnsResponseTimeSample sample)
    {
        lock (_lock)
        {
            if (_windows.TryGetValue(resolverIp, out var q) && q.Count > 0)
            {
                sample = q.Last();
                return true;
            }
            sample = default!;
            return false;
        }
    }
}
