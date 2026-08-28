using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace TaskManagerPlus.Services;

/// <summary>One address family's half of a #592 comparison.</summary>
public sealed record AddressFamilyProbeResult(
    bool Resolved, string? Address, bool Connected, double? ConnectMs, string? FailureReason)
{
    /// <summary>Plain status text for the view - same "compute it once in C#" tradeoff this app's
    /// other bool-to-text record properties (FirewallProfileStatus.EnabledText, ...) already take.</summary>
    public string ConnectedText => !Resolved ? "No record" : Connected ? "Connected" : "Failed";
}

/// <summary>#592's full comparison result for one hostname.</summary>
public sealed record DualStackReachabilityResult(
    string Hostname, AddressFamilyProbeResult V4, AddressFamilyProbeResult V6, string Verdict);

/// <summary>
/// Item #592: resolves both A and AAAA records for a chosen hostname and times a TCP connect over
/// each family separately - the classic "AAAA is advertised but IPv6 is actually broken" case pays a
/// several-second stall (every dual-stack OS tries IPv6 first, then falls back) that a single
/// combined connectivity test hides. Doing the two probes in parallel and reporting each family's own
/// timing/outcome side by side turns that invisible stall into an explicit, attributable number.
///
/// A plain TCP connect to port 443 (HTTPS' default) is used as the "is this address actually
/// reachable" test rather than ICMP - many hosts/CDNs block ICMP echo outright while still serving
/// real traffic, so a ping-based test would misreport a perfectly working host as unreachable over
/// whichever family blocks ICMP. Bounded to a generous 5s per family so a genuinely black-holed IPv6
/// path (packets sent, nothing ever comes back) still resolves to a definite "no" instead of hanging
/// the UI's command indefinitely.
/// </summary>
public static class DualStackReachabilityService
{
    private const int Port = 443;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static async Task<DualStackReachabilityResult> CompareAsync(string hostname)
    {
        var v4Task = ProbeAsync(hostname, AddressFamily.InterNetwork);
        var v6Task = ProbeAsync(hostname, AddressFamily.InterNetworkV6);
        await Task.WhenAll(v4Task, v6Task);
        var v4 = v4Task.Result;
        var v6 = v6Task.Result;

        string verdict = BuildVerdict(v4, v6);
        return new DualStackReachabilityResult(hostname, v4, v6, verdict);
    }

    private static string BuildVerdict(AddressFamilyProbeResult v4, AddressFamilyProbeResult v6)
    {
        if (!v4.Resolved && !v6.Resolved)
            return "Neither an A nor a AAAA record resolved for this host - check the hostname, or DNS resolution itself is broken.";

        if (v6.Resolved && !v6.Connected && v4.Resolved && v4.Connected)
        {
            string wait = v6.ConnectMs is { } ms ? $"{ms:0} ms before giving up" : "no reply at all";
            return $"AAAA is advertised for this host but the IPv6 connection failed ({wait}) while IPv4 connected in {v4.ConnectMs:0} ms - " +
                   "the classic \"AAAA is advertised but IPv6 is broken\" case: every dual-stack app tries IPv6 first, so this host pays that stall on every connection. Quick flag, not a verdict.";
        }

        if (!v6.Resolved && v4.Resolved)
            return v4.Connected ? "No AAAA record - this host is IPv4-only, and IPv4 connects fine." : "No AAAA record, and the IPv4 connection itself failed.";

        if (!v4.Resolved && v6.Resolved)
            return v6.Connected ? "No A record - this host is IPv6-only, and IPv6 connects fine." : "No A record, and the IPv6 connection itself failed.";

        if (v4.Connected && v6.Connected)
        {
            double delta = (v6.ConnectMs ?? 0) - (v4.ConnectMs ?? 0);
            return delta > 250
                ? $"Both families connected, but IPv6 took {delta:0} ms longer ({v6.ConnectMs:0} ms vs. IPv4's {v4.ConnectMs:0} ms) - worth a look."
                : $"Both families connected cleanly - IPv4 {v4.ConnectMs:0} ms, IPv6 {v6.ConnectMs:0} ms.";
        }

        if (!v4.Connected && !v6.Connected)
            return "Both families resolved, but neither TCP connection succeeded - likely a firewall/proxy blocking outbound port 443, not an IPv4-vs-IPv6 issue specifically.";

        return "Inconclusive.";
    }

    private static async Task<AddressFamilyProbeResult> ProbeAsync(string hostname, AddressFamily family)
    {
        IPAddress? address;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname);
            address = addresses.FirstOrDefault(a => a.AddressFamily == family);
        }
        catch (Exception ex)
        {
            return new AddressFamilyProbeResult(false, null, false, null, $"DNS lookup failed: {ex.Message}");
        }

        if (address is null)
            return new AddressFamilyProbeResult(false, null, false, null, null);

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient(family);
            using var cts = new CancellationTokenSource(ConnectTimeout);
            await client.ConnectAsync(address, Port, cts.Token);
            sw.Stop();
            return new AddressFamilyProbeResult(true, address.ToString(), true, sw.Elapsed.TotalMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new AddressFamilyProbeResult(true, address.ToString(), false, sw.Elapsed.TotalMilliseconds, "Timed out");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new AddressFamilyProbeResult(true, address.ToString(), false, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
    }
}
