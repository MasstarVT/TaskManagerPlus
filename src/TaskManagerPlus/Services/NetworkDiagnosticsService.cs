using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TaskManagerPlus.Services;

/// <summary>Result of one connectivity check - see <see cref="NetworkDiagnosticsService.CheckAsync"/>.</summary>
public sealed record ConnectivityResult(
    bool? GatewayReachable, long GatewayRoundtripMs,
    bool? DnsReachable, long DnsRoundtripMs);

/// <summary>
/// Answers "is my router even reachable" / "can I reach the internet at all" with a quick ICMP
/// ping - the two most basic connectivity questions someone troubleshooting a network problem
/// asks first, before digging into throughput graphs or adapter settings.
/// </summary>
public sealed class NetworkDiagnosticsService
{
    private const int TimeoutMs = 1200;

    /// <summary>
    /// Pings the default gateway (the first one reported by any active, non-loopback adapter)
    /// and a well-known public DNS resolver (1.1.1.1). A null result (rather than false) means
    /// the check itself couldn't run (e.g. no gateway configured at all) - distinct from a ping
    /// that actually failed. ICMP being blocked by a firewall is a real, known limitation: a
    /// "false" here means "didn't respond to ping", not definitively "unreachable".
    /// </summary>
    public async Task<ConnectivityResult> CheckAsync()
    {
        var gatewayTask = PingAsync(FindDefaultGateway());
        var dnsTask = PingAsync("1.1.1.1");
        await Task.WhenAll(gatewayTask, dnsTask);

        var (gatewayOk, gatewayMs) = gatewayTask.Result;
        var (dnsOk, dnsMs) = dnsTask.Result;
        return new ConnectivityResult(gatewayOk, gatewayMs, dnsOk, dnsMs);
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
