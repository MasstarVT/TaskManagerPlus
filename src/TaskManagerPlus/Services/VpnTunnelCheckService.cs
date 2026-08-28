using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>#577's result - a real behavioural check, distinct from the existing #37 VPN
/// name-substring presence heuristic (NetworkDiagnosticsService.ReadActiveVpnAdapterNames). Every
/// "?" field is null/hedged when this app has a VPN present but genuinely can't tell (never
/// guessed).</summary>
public sealed record VpnTunnelCheckResult(
    bool HasVpn, string? VpnAdapterName,
    string? DefaultRouteAdapterName, bool? VpnHoldsDefaultRoute, string DefaultRouteExplanation,
    string TestHostname, string? RespondingResolverIp, bool? ResolverLooksOutsideVpn, string DnsLeakExplanation);

/// <summary>
/// Item #577 (suggestions.md "Proxy, PAC, VPN and Winsock"): two independent behavioural checks
/// against whichever adapter the existing #37 name-substring heuristic flags as a VPN, both quick
/// flags rather than verdicts:
///
/// 1. Default-route ownership - does the VPN adapter actually hold the 0.0.0.0/0 route at the
///    lowest metric (full tunnel), or does some other adapter still win (split tunnel)? Reuses
///    RoutingTableService's own `route print` parse (#513) rather than re-implementing routing-table
///    enumeration a second time.
/// 2. DNS-leak check - which resolver actually answers a plain `nslookup &lt;host&gt;` with no
///    explicit server argument (i.e. whichever resolver Windows' own default resolution order
///    picks), compared against the VPN adapter's own configured resolver(s). A resolver outside
///    that set - while the VPN otherwise appears to hold the default route - is the behavioural
///    signature of a DNS leak; this app has no way to see which *physical* path a query actually
///    took, so the flag is worded as a possibility, not a proof.
/// </summary>
public static class VpnTunnelCheckService
{
    private const string TestHostname = "www.msftconnecttest.com";

    public static async Task<VpnTunnelCheckResult> CheckAsync()
    {
        var vpnNames = NetworkDiagnosticsService.ReadActiveVpnAdapterNames();
        bool hasVpn = vpnNames.Count > 0;
        string? vpnAdapterName = vpnNames.FirstOrDefault();

        var routes = await RoutingTableService.GetActiveRoutesAsync();
        var bestDefault = routes
            .Where(r => r.AddressFamily == "IPv4" && r.IsDefaultRoute)
            .OrderBy(r => r.Metric)
            .FirstOrDefault();

        string? defaultRouteAdapterName = bestDefault is not null ? FindAdapterByLocalIp(bestDefault.InterfaceLabel) : null;

        bool? vpnHoldsDefault;
        string routeExplanation;
        if (!hasVpn)
        {
            vpnHoldsDefault = null;
            routeExplanation = "No VPN-looking adapter is currently active.";
        }
        else if (bestDefault is null || defaultRouteAdapterName is null)
        {
            vpnHoldsDefault = null;
            routeExplanation = "Couldn't determine which adapter currently holds the default (0.0.0.0/0) route.";
        }
        else
        {
            vpnHoldsDefault = vpnNames.Contains(defaultRouteAdapterName, StringComparer.OrdinalIgnoreCase);
            routeExplanation = vpnHoldsDefault == true
                ? $"\"{defaultRouteAdapterName}\" (VPN) holds the default route at metric {bestDefault.Metric} - looks like a full tunnel: traffic with no more specific route goes through the VPN."
                : $"The default route is held by \"{defaultRouteAdapterName}\", not the VPN adapter (\"{vpnAdapterName}\") - looks like a split tunnel: only traffic the VPN explicitly routes goes through it, everything else uses your normal connection.";
        }

        var (resolverIp, dnsError) = await QueryDefaultResolverAsync(TestHostname);
        bool? resolverOutsideVpn;
        string dnsExplanation;
        if (!hasVpn)
        {
            resolverOutsideVpn = null;
            dnsExplanation = "No VPN-looking adapter is currently active.";
        }
        else if (resolverIp is null)
        {
            resolverOutsideVpn = null;
            dnsExplanation = dnsError ?? "Couldn't determine which resolver answered.";
        }
        else
        {
            var vpnResolvers = DnsResolverService.ReadConfiguredResolvers()
                .Where(a => vpnNames.Contains(a.AdapterName, StringComparer.OrdinalIgnoreCase))
                .SelectMany(a => a.ResolverIps)
                .ToList();
            bool answeredByVpnResolver = vpnResolvers.Contains(resolverIp, StringComparer.OrdinalIgnoreCase);
            resolverOutsideVpn = !answeredByVpnResolver;

            dnsExplanation = answeredByVpnResolver
                ? $"The test query was answered by {resolverIp}, one of the VPN's own configured resolver(s) - DNS looks tunneled."
                : vpnResolvers.Count == 0
                    ? $"The test query was answered by {resolverIp}. The VPN adapter doesn't advertise its own DNS resolver, so this can't confirm whether DNS is tunneled or not."
                    : $"The test query was answered by {resolverIp}, which isn't one of the VPN's own configured resolver(s) ({string.Join(", ", vpnResolvers)}) - possible DNS leak outside the tunnel. Quick flag, not a verdict - a resolver IP alone can't prove which physical path the query took.";
        }

        return new VpnTunnelCheckResult(hasVpn, vpnAdapterName, defaultRouteAdapterName, vpnHoldsDefault, routeExplanation,
            TestHostname, resolverIp, resolverOutsideVpn, dnsExplanation);
    }

    private static string? FindAdapterByLocalIp(string ip)
    {
        if (!IPAddress.TryParse(ip, out var target)) return null;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(target)))
                    return ni.Name;
            }
        }
        catch
        {
            // Best-effort.
        }
        return null;
    }

    /// <summary>Shells out to `nslookup &lt;hostname&gt;` with no explicit server argument - the
    /// same known-tool approach DnsResolverService (#517) already takes, but deliberately omitting
    /// the server here so nslookup queries whichever resolver Windows' own default resolution order
    /// actually picks, and its own "Server:"/"Address:" header block reports exactly which one that
    /// was - the real behavioural answer this check needs, not a guess.</summary>
    private static async Task<(string? ResolverIp, string? Error)> QueryDefaultResolverAsync(string hostname)
    {
        try
        {
            var psi = new ProcessStartInfo("nslookup.exe", hostname)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (null, "Couldn't start nslookup.exe.");

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(5000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return (null, "nslookup timed out.");
            }

            string output = (await outputTask) + (await errorTask);
            string normalized = output.Replace("\r\n", "\n");
            // nslookup's own header block (before the blank-line separator from the answer block):
            //   Server:  resolver.example.com
            //   Address:  192.168.1.1
            string headerBlock = normalized.Split("\n\n")[0];
            var match = Regex.Match(headerBlock, @"Address:\s*([0-9a-fA-F:.]+)");
            return match.Success ? (match.Groups[1].Value.Trim(), null) : (null, "Couldn't parse nslookup's server header - it may not be installed or DNS may be entirely unreachable.");
        }
        catch (Exception ex)
        {
            return (null, $"Lookup failed: {ex.Message}");
        }
    }
}
