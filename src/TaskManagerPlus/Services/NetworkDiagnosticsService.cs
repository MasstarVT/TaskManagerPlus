using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>Result of one connectivity check - see <see cref="NetworkDiagnosticsService.CheckAsync"/>.</summary>
public sealed record ConnectivityResult(
    bool? GatewayReachable, long GatewayRoundtripMs,
    bool? DnsReachable, long DnsRoundtripMs,
    /// <summary>Time to actually resolve a hostname (#33) - distinct from the ICMP ping to a
    /// resolver IP above, which never exercises real DNS resolution. Null on failure/timeout.</summary>
    long? DnsLookupMs,
    /// <summary>Captive portal detection (round 9, #51) - null when there's no internet path to
    /// even check (mirrors GatewayReachable/DnsReachable's null convention).</summary>
    bool? CaptivePortalDetected);

/// <summary>One active network adapter's negotiated link speed (#31).</summary>
public sealed record AdapterLinkInfo(string Name, double SpeedMbps, bool LooksDegraded);

/// <summary>Read-only WinHTTP/IE proxy configuration (round 9, #47) - display only, this app never
/// writes to these keys.</summary>
public sealed record ProxyConfigInfo(bool Enabled, string ProxyServer, string AutoConfigUrl);

/// <summary>One network adapter's driver version/date (round 9, #48).</summary>
public sealed record AdapterDriverInfo(string DeviceName, string DriverVersion, DateTime? DriverDate, bool LooksOld);

/// <summary>Min/max/avg round-trip and packet loss over N pings (round 9, #50) - a jitter/loss
/// quick test, distinct from the single-shot gateway/DNS ping above. <see cref="JitterMs"/> is
/// the original mean-absolute-deviation figure; <see cref="RfcJitterMs"/> (#507) is the same
/// samples run through the proper RFC 3550 smoothed interarrival-jitter formula, and
/// <see cref="MosEstimate"/> (#507) is a rough VoIP/gaming call-quality estimate derived from
/// latency + jitter + loss - see NetworkDiagnosticsService.EstimateMos's remarks for why it's
/// explicitly an estimate, not a measurement of an actual call.</summary>
public sealed record JitterTestResult(string Host, int Sent, int Received, double LossPercent, long MinMs, long MaxMs, double AvgMs, double JitterMs, double RfcJitterMs, double MosEstimate, string Message);

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
        var captivePortalTask = CheckCaptivePortalAsync();
        await Task.WhenAll(gatewayTask, dnsTask, dnsLookupTask, captivePortalTask);

        var (gatewayOk, gatewayMs) = gatewayTask.Result;
        var (dnsOk, dnsMs) = dnsTask.Result;
        return new ConnectivityResult(gatewayOk, gatewayMs, dnsOk, dnsMs, dnsLookupTask.Result, captivePortalTask.Result);
    }

    private static readonly HttpClient CaptivePortalHttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, // a captive portal's login page is typically served via a redirect - following it would hide the very signal we're checking for
        UseProxy = false,
    })
    { Timeout = TimeSpan.FromSeconds(4) };

    private const string CaptivePortalUrl = "http://www.msftconnecttest.com/connecttest.txt";
    private const string CaptivePortalExpectedBody = "Microsoft Connect Test";

    /// <summary>
    /// Captive portal detection (#51) - the same NCSI-style check Windows itself uses (an HTTP GET
    /// to a fixed Microsoft URL expecting an exact plaintext response), distinct from the
    /// ICMP-based gateway/DNS checks above. A captive portal (airport/hotel/coffee-shop Wi-Fi login
    /// page) typically answers with a redirect or its own HTML instead of the expected literal
    /// body - either one, or the request failing outright with connectivity otherwise present,
    /// counts as "portal detected". Null means "couldn't tell" (e.g. no network path at all, so
    /// there's nothing to distinguish a portal from), not a confirmed answer either way.
    /// </summary>
    private static async Task<bool?> CheckCaptivePortalAsync()
    {
        try
        {
            using var response = await CaptivePortalHttpClient.GetAsync(CaptivePortalUrl);
            if ((int)response.StatusCode is >= 300 and < 400) return true; // redirected - classic portal behavior
            if (!response.IsSuccessStatusCode) return null; // no clean answer either way

            string body = (await response.Content.ReadAsStringAsync()).Trim();
            return !body.Equals(CaptivePortalExpectedBody, StringComparison.Ordinal);
        }
        catch
        {
            // Couldn't even reach the URL - could be no internet at all (not a portal), so this
            // deliberately returns null rather than guessing "portal detected".
            return null;
        }
    }

    /// <summary>Read-only WinHTTP/IE proxy configuration (#47), from the per-user Internet
    /// Settings registry key - the same source `netsh winhttp show proxy`/Internet Options reads
    /// from. Display only.</summary>
    public static ProxyConfigInfo ReadProxyConfig()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings");
            bool enabled = key?.GetValue("ProxyEnable") is int e && e != 0;
            string server = (key?.GetValue("ProxyServer") as string ?? string.Empty).Trim();
            string autoConfigUrl = (key?.GetValue("AutoConfigURL") as string ?? string.Empty).Trim();
            return new ProxyConfigInfo(enabled, server, autoConfigUrl);
        }
        catch
        {
            return new ProxyConfigInfo(false, string.Empty, string.Empty);
        }
    }

    // Same 2-year "worth checking for an update" bar SystemSpecsService.ReadOutdatedDrivers uses -
    // deliberately not a claim that a newer version is actually known to exist online (this app
    // makes no network lookup for that), just the same date-based heuristic applied to the Net
    // device class specifically.
    private static readonly DateTime DriverAgeCutoff = DateTime.Now.AddYears(-2);

    /// <summary>
    /// Network adapter driver version/date (#48), via Win32_PnPSignedDriver filtered to
    /// DeviceClass='Net' - the same WMI class SystemSpecsService.ReadOutdatedDrivers already
    /// queries for a different device-class allowlist. Deliberately does NOT claim to know
    /// whether a newer driver is actually available anywhere - there's no online lookup here -
    /// LooksOld is exactly the same "old enough to be worth a manual check" date heuristic
    /// ReadOutdatedDrivers already uses, applied to network adapters specifically, and the UI
    /// presents it with that same honesty.
    /// </summary>
    public static List<AdapterDriverInfo> ReadAdapterDriverInfo()
    {
        var drivers = new List<AdapterDriverInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceName, Manufacturer, DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceClass = 'NET'");
            foreach (ManagementObject mo in searcher.Get())
            {
                string deviceName = (mo["DeviceName"] as string ?? string.Empty).Trim();
                if (deviceName.Length == 0) continue;

                string manufacturer = (mo["Manufacturer"] as string ?? string.Empty).Trim();
                // Virtual/software adapters (loopback, tunnel miniports, "WAN Miniport", Hyper-V
                // switches, ...) aren't something a driver-update check is meaningful for.
                if (deviceName.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
                    deviceName.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) ||
                    deviceName.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    deviceName.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase))
                    continue;

                DateTime? driverDate = null;
                if (mo["DriverDate"] is string wmiDate)
                {
                    try { driverDate = ManagementDateTimeConverter.ToDateTime(wmiDate); } catch { /* leave null */ }
                }
                bool looksOld = driverDate is { } d && d.Year > 2006 && d < DriverAgeCutoff;

                drivers.Add(new AdapterDriverInfo(deviceName, (mo["DriverVersion"] as string ?? string.Empty).Trim(), driverDate, looksOld));
            }
        }
        catch
        {
            // return whatever was gathered before the failure
        }
        return drivers;
    }

    /// <summary>
    /// Jitter/packet-loss quick test (#50) - N sequential pings to a host, reporting min/max/avg
    /// round-trip and loss percentage, alongside the existing single-shot gateway/DNS latency
    /// reading above. On-demand only (a button + host field), never on the periodic timer - a
    /// 10-ping test takes several seconds and is only useful when actively diagnosing a
    /// connection, the same "expensive, so make it explicit" tradeoff this app already takes for
    /// its other on-demand diagnostics.
    /// </summary>
    public static async Task<JitterTestResult> RunJitterTestAsync(string host, int count = 10)
    {
        var times = new List<long>();
        int sent = 0, received = 0;
        try
        {
            using var ping = new Ping();
            for (int i = 0; i < count; i++)
            {
                sent++;
                try
                {
                    var reply = await ping.SendPingAsync(host, TimeoutMs);
                    if (reply.Status == IPStatus.Success) { received++; times.Add(reply.RoundtripTime); }
                }
                catch
                {
                    // one failed ping shouldn't abort the whole test
                }
                if (i < count - 1) await Task.Delay(200);
            }
        }
        catch (Exception ex)
        {
            return new JitterTestResult(host, sent, received, 100, 0, 0, 0, 0, 0, 1.0, $"Test failed: {ex.Message}");
        }

        double lossPercent = sent == 0 ? 100 : (sent - received) * 100.0 / sent;
        if (times.Count == 0)
            return new JitterTestResult(host, sent, received, lossPercent, 0, 0, 0, 0, 0, 1.0, "No successful replies.");

        long min = times.Min(), max = times.Max();
        double avg = times.Average();
        // Jitter here is mean absolute deviation between consecutive samples - a simple, standard
        // "how much does the latency wobble" figure, not a formal RFC 3550 jitter calculation.
        double jitter = times.Count < 2 ? 0 : times.Zip(times.Skip(1), (a, b) => Math.Abs(b - a)).Average();

        // #507: the actual RFC 3550 smoothed interarrival jitter, plus a rough MOS-style call
        // quality estimate derived from it.
        double rfcJitter = ComputeRfc3550Jitter(times);
        double mos = EstimateMos(avg, rfcJitter, lossPercent);

        return new JitterTestResult(host, sent, received, lossPercent, min, max, avg, jitter, rfcJitter, mos,
            $"{received}/{sent} replies ({lossPercent:0.#}% loss) — min {min} ms, avg {avg:0.#} ms, max {max} ms, jitter {jitter:0.#} ms " +
            $"(RFC 3550: {rfcJitter:0.#} ms). Estimated call quality (MOS, not a measurement of an actual call): {mos:0.0}/4.5 ({MosQualityLabel(mos)}).");
    }

    /// <summary>#507: RFC 3550 §6.4.1's smoothed interarrival jitter formula, applied to
    /// round-trip samples - ICMP only gives us round-trip timing, not the one-way send/receive
    /// timestamps the RFC actually assumes, so this is the same "good enough" approximation
    /// consumer network-quality tools commonly use, not a strict protocol-compliance claim.
    /// J(i) = J(i-1) + (|D(i-1,i)| - J(i-1)) / 16, the RFC's own gain factor.</summary>
    public static double ComputeRfc3550Jitter(IReadOnlyList<long> roundtripsMs)
    {
        if (roundtripsMs.Count < 2) return 0;
        double j = 0;
        for (int i = 1; i < roundtripsMs.Count; i++)
        {
            double d = Math.Abs(roundtripsMs[i] - roundtripsMs[i - 1]);
            j += (d - j) / 16.0;
        }
        return j;
    }

    /// <summary>#507: a rough MOS (Mean Opinion Score, 1.0-4.5) estimate from the simplified
    /// ITU-T G.107 E-model, folding in latency, jitter, and loss the same way several consumer
    /// VoIP-quality tools do. Explicitly an estimate of what a call would probably sound like
    /// over this path right now, NOT a measurement of any actual call - this app places no VoIP
    /// call to measure. "Quick flag, not a verdict" applies here as much as anywhere else in this
    /// app's heuristics.</summary>
    public static double EstimateMos(double avgRoundtripMs, double jitterMs, double lossPercent)
    {
        // Effective latency folds one-way delay (approximated as half the round-trip) and
        // jitter's playout-buffer cost into a single "how much delay does this really feel like"
        // figure - the same shape of adjustment the E-model's Id term is built from.
        double effectiveLatencyMs = (avgRoundtripMs / 2.0) + (jitterMs * 2.0) + 10.0;

        double r = effectiveLatencyMs < 160
            ? 93.2 - (effectiveLatencyMs / 40.0)
            : 93.2 - ((effectiveLatencyMs - 120.0) / 10.0);

        r -= lossPercent * 2.5; // packet loss directly erodes the R-factor
        r = Math.Clamp(r, 0, 100);

        double mos = 1 + 0.035 * r + 0.000007 * r * (r - 60) * (100 - r);
        return Math.Clamp(mos, 1.0, 4.5);
    }

    private static string MosQualityLabel(double mos) => mos switch
    {
        >= 4.0 => "excellent",
        >= 3.6 => "good",
        >= 3.1 => "fair",
        >= 2.6 => "poor",
        _ => "bad",
    };

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

    /// <summary>The default gateway of the first active, non-loopback/non-tunnel adapter with
    /// one configured - exposed publicly (originally private to this class) so
    /// LatencyMonitorService's continuous probe ring (#501/#503) can target the same gateway
    /// this tab's existing one-shot connectivity check does, rather than re-deriving it.</summary>
    public static string? FindDefaultGateway()
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
