using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>
/// Path MTU discovery result (#510) plus the derived PMTUD black-hole verdict (#512) from the
/// same sweep. <see cref="DiscoveredMtu"/>/<see cref="PayloadSize"/> are null when even a minimal
/// probe failed - degrade to "couldn't measure", never a fabricated number.
/// </summary>
public sealed record PathMtuResult(
    string Host,
    int? DiscoveredMtu,
    int? PayloadSize,
    string Message,
    /// <summary>#512: true when a large DF ping failed *silently* (no ICMP "fragmentation
    /// needed" reply) right after a tiny DF ping to the same host succeeded - the specific
    /// signature of a router dropping oversized packets without telling anyone.</summary>
    bool BlackHoleSuspected,
    string? BlackHoleMessage,
    IReadOnlyList<string> ProbeLog)
{
    public static PathMtuResult Failed(string host, string message) =>
        new(host, null, null, message, false, null, Array.Empty<string>());
}

/// <summary>
/// Item #510 (suggestions.md "Path MTU, routing and hop-level diagnostics"): binary-searches the
/// largest ICMP payload that survives a DontFragment=true ping to a chosen host, then reports the
/// discovered path MTU (payload + 28 bytes of IPv4/ICMP header). Deliberately on-demand only (a
/// button, not a timer) - a full binary search over the 28-1472 byte payload range is a dozen-plus
/// round trips, the same "expensive, so make it explicit" tradeoff RunJitterTestAsync/
/// TracerouteService already take on this tab.
///
/// Also derives #512's PMTUD black-hole verdict from the same sweep: when a minimal probe
/// succeeds but a probe at the standard 1472-byte payload fails without the router replying with
/// ICMP "fragmentation needed" (IPStatus.PacketTooBig - it just times out instead), that's the
/// specific signature of "web pages hang halfway, SSH works fine", not ordinary packet loss.
/// Quick flag, not a verdict - a single lost probe can look the same as a real black hole.
/// </summary>
public static class PathMtuService
{
    private const int TimeoutMs = 1500;

    // Standard IPv4 Ethernet MTU (1500) minus 20 bytes of IP header + 8 bytes of ICMP header.
    // A path MTU above 1500 (jumbo frames) is out of scope here - the well-known values this
    // sweep exists to flag (PPPoE/VPN/tunnel overhead) are all well under it.
    private const int MinPayload = 28;
    private const int MaxPayload = 1472;

    private static readonly Regex ValidHostRegex = new(@"^[A-Za-z0-9][A-Za-z0-9.\-:]*$", RegexOptions.Compiled);

    public static async Task<PathMtuResult> DiscoverAsync(string host)
    {
        host = host.Trim();
        if (host.Length == 0) return PathMtuResult.Failed(host, "Enter a host name or IP address first.");
        if (host.Length > 255 || !ValidHostRegex.IsMatch(host))
            return PathMtuResult.Failed(host, "That doesn't look like a valid host name or IP address.");

        var log = new List<string>();
        try
        {
            // Baseline: a tiny DF probe has to succeed before the rest of the sweep means anything.
            var baseline = await ProbeAsync(host, MinPayload);
            log.Add($"{MinPayload}-byte payload -> {DescribeStatus(baseline)}");
            if (baseline is not { Status: IPStatus.Success })
                return PathMtuResult.Failed(host,
                    $"Host didn't respond to a minimal {MinPayload}-byte DF ping - can't measure path MTU (unreachable, or ICMP is blocked).");

            // #512: probe the standard payload size first, both because it answers "is the full
            // path MTU already fine" in one round trip, and because comparing it against the
            // baseline above is exactly the black-hole signature this item exists to catch.
            var maxProbe = await ProbeAsync(host, MaxPayload);
            log.Add($"{MaxPayload}-byte payload -> {DescribeStatus(maxProbe)}");
            bool blackHoleSuspected = maxProbe is not null
                && maxProbe.Status != IPStatus.Success
                && maxProbe.Status != IPStatus.PacketTooBig;

            int discoveredPayload;
            if (maxProbe is { Status: IPStatus.Success })
            {
                discoveredPayload = MaxPayload;
            }
            else
            {
                // Binary search the boundary between MinPayload (known good) and MaxPayload
                // (known bad) - about a dozen round trips for the full 28-1472 byte range.
                int lo = MinPayload, hi = MaxPayload, best = MinPayload;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    var reply = await ProbeAsync(host, mid);
                    log.Add($"{mid}-byte payload -> {DescribeStatus(reply)}");
                    if (reply is { Status: IPStatus.Success }) { best = mid; lo = mid + 1; }
                    else { hi = mid - 1; }
                }
                discoveredPayload = best;
            }

            int discoveredMtu = discoveredPayload + 28;
            string? wellKnownFlag = FlagWellKnownMtu(discoveredMtu);

            string message = $"Discovered path MTU to {host}: {discoveredMtu} bytes ({discoveredPayload}-byte payload + 28-byte IP/ICMP header).";
            if (wellKnownFlag is not null) message += " " + wellKnownFlag;

            string? blackHoleMessage = blackHoleSuspected
                ? $"Possible PMTUD black hole: the {MaxPayload}-byte probe failed silently (no ICMP \"fragmentation needed\" reply came back), " +
                  $"right after the {MinPayload}-byte probe succeeded - a router on this path may be dropping oversized packets without telling " +
                  "anyone, the classic \"web pages hang halfway, SSH works fine\" cause. Quick flag, not a verdict - a single lost probe can look the same."
                : null;

            return new PathMtuResult(host, discoveredMtu, discoveredPayload, message, blackHoleSuspected, blackHoleMessage, log);
        }
        catch (Exception ex)
        {
            return PathMtuResult.Failed(host, $"Path MTU discovery failed: {ex.Message}");
        }
    }

    private static async Task<PingReply?> ProbeAsync(string host, int payloadSize)
    {
        try
        {
            using var ping = new Ping();
            var buffer = new byte[payloadSize];
            for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)'a';
            var options = new PingOptions { DontFragment = true, Ttl = 128 };
            return await ping.SendPingAsync(host, TimeoutMs, buffer, options);
        }
        catch
        {
            // Resolution failure, unreachable, or an unexpected interop error - treated the same
            // as an ordinary timeout by the caller.
            return null;
        }
    }

    private static string DescribeStatus(PingReply? reply) => reply switch
    {
        null => "error",
        { Status: IPStatus.Success } => "OK",
        { Status: IPStatus.PacketTooBig } => "fragmentation needed (router replied)",
        { Status: IPStatus.TimedOut } => "timed out (no reply)",
        var r => r.Status.ToString(),
    };

    /// <summary>Flags the well-known MTU values a mismatch commonly lands on - PPPoE, IPsec/
    /// WireGuard VPN overhead, and a generic tunnel figure. A heuristic band around each published
    /// number, not an exact-match requirement, and explicitly informational - see this class's own
    /// remarks and CLAUDE.md's "quick flag, not a verdict" convention.</summary>
    private static string? FlagWellKnownMtu(int mtu) => mtu switch
    {
        1500 => null, // standard Ethernet - nothing to flag
        >= 1488 and <= 1496 => "This looks like a PPPoE-limited path (typically 1492) - worth a manual check, not a confirmed cause.",
        >= 1414 and <= 1426 => "This looks like IPsec/WireGuard VPN tunnel overhead (typically ~1420) - worth a manual check, not a confirmed cause.",
        >= 1396 and <= 1404 => "This looks like a generic tunneled path (typically 1400, e.g. some VPN/GRE tunnels) - worth a manual check, not a confirmed cause.",
        _ => null,
    };
}
