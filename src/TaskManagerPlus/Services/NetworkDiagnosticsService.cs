using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TaskManagerPlus.Services;

/// <summary>Result of one connectivity check - see <see cref="NetworkDiagnosticsService.CheckAsync"/>.</summary>
public sealed record ConnectivityResult(
    bool? GatewayReachable, long GatewayRoundtripMs,
    bool? DnsReachable, long DnsRoundtripMs,
    /// <summary>Time to actually resolve a hostname (#33) - distinct from the ICMP ping to a
    /// resolver IP above, which never exercises real DNS resolution. Null on failure/timeout.</summary>
    long? DnsLookupMs);

/// <summary>One active network adapter's negotiated link speed (#31).</summary>
public sealed record AdapterLinkInfo(string Name, double SpeedMbps, bool LooksDegraded);

/// <summary>
/// Answers "is my router even reachable" / "can I reach the internet at all" with a quick ICMP
/// ping - the two most basic connectivity questions someone troubleshooting a network problem
/// asks first, before digging into throughput graphs or adapter settings.
/// </summary>
public sealed class NetworkDiagnosticsService
{
    private const int TimeoutMs = 1200;

    // Windows' own connectivity-check hostname (used by NCSI) - a neutral choice for timing an
    // actual DNS resolution rather than an arbitrary third-party domain.
    private const string DnsLookupHostname = "www.msftconnecttest.com";

    // Substrings that show up in a VPN adapter's interface description/name across the common
    // clients (#37) - a heuristic, not an exhaustive list, same "good enough for a quick flag"
    // tradeoff as the process signature check.
    private static readonly string[] VpnNameHints =
    {
        "vpn", "wireguard", "tap-windows", "tap0", "tun0", "openvpn", "anyconnect",
        "nordvpn", "expressvpn", "zscaler", "globalprotect", "fortinet", "pritunl",
    };

    /// <summary>
    /// Pings the default gateway (the first one reported by any active, non-loopback adapter)
    /// and a well-known public DNS resolver (1.1.1.1), and times an actual hostname resolution.
    /// A null result (rather than false) means the check itself couldn't run (e.g. no gateway
    /// configured at all) - distinct from a ping that actually failed. ICMP being blocked by a
    /// firewall is a real, known limitation: a "false" here means "didn't respond to ping", not
    /// definitively "unreachable".
    /// </summary>
    public async Task<ConnectivityResult> CheckAsync()
    {
        var gatewayTask = PingAsync(FindDefaultGateway());
        var dnsTask = PingAsync("1.1.1.1");
        var dnsLookupTask = TimeDnsLookupAsync();
        await Task.WhenAll(gatewayTask, dnsTask, dnsLookupTask);

        var (gatewayOk, gatewayMs) = gatewayTask.Result;
        var (dnsOk, dnsMs) = dnsTask.Result;
        return new ConnectivityResult(gatewayOk, gatewayMs, dnsOk, dnsMs, dnsLookupTask.Result);
    }

    private static async Task<long?> TimeDnsLookupAsync()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeoutMs);
            await Dns.GetHostEntryAsync(DnsLookupHostname, cts.Token);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        catch
        {
            // Resolution failed or timed out - a real, reportable "DNS is slow/broken" signal,
            // not an app error.
            return null;
        }
    }

    /// <summary>Negotiated link speed per active adapter (#31) - flags a "Gigabit"-branded
    /// adapter that negotiated down to under 1 Gbps, a classic bad-cable/bad-port symptom.
    /// No I/O here (pure NetworkInterface enumeration), so it's cheap to call on every tick of
    /// whichever timer calls it.</summary>
    public static List<AdapterLinkInfo> ReadAdapterLinks()
    {
        var links = new List<AdapterLinkInfo>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (ni.Speed <= 0) continue; // virtual/software adapters commonly report -1 or 0

                double mbps = ni.Speed / 1_000_000.0;
                bool looksGigabit = ni.Description.Contains("Gigabit", StringComparison.OrdinalIgnoreCase) ||
                                     ni.Description.Contains("GbE", StringComparison.OrdinalIgnoreCase);
                bool degraded = looksGigabit && mbps > 0 && mbps < 1000;

                links.Add(new AdapterLinkInfo(ni.Name, mbps, degraded));
            }
        }
        catch
        {
            // Best-effort - return whatever was gathered.
        }
        return links;
    }

    /// <summary>Active VPN-looking adapter names (#37), via a name/description substring
    /// heuristic plus PPP/Tunnel interface types - not a definitive "traffic is actually routed
    /// through it" check, just a presence flag.</summary>
    public static List<string> ReadActiveVpnAdapterNames()
    {
        var names = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                bool looksVpn = ni.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
                    || VpnNameHints.Any(hint =>
                        ni.Name.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
                        ni.Description.Contains(hint, StringComparison.OrdinalIgnoreCase));
                if (looksVpn) names.Add(ni.Name);
            }
        }
        catch
        {
            // Best-effort.
        }
        return names;
    }

    private static string? FindDefaultGateway()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var gateway = ni.GetIPProperties().GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                if (gateway is not null) return gateway.Address.ToString();
            }
        }
        catch
        {
            // fall through to "no gateway found"
        }
        return null;
    }

    private static async Task<(bool? Ok, long Ms)> PingAsync(string? host)
    {
        if (string.IsNullOrEmpty(host)) return (null, 0);

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, TimeoutMs);
            return reply.Status == IPStatus.Success ? (true, reply.RoundtripTime) : (false, 0);
        }
        catch
        {
            // Host unreachable, no route, or ICMP blocked outright - treat as "didn't respond"
            // rather than letting the exception propagate into the caller's timer loop.
            return (false, 0);
        }
    }
}
