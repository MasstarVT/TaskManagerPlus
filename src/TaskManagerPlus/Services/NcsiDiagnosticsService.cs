using System.Net;
using System.Net.Http;
using System.ServiceProcess;
using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>The NCSI active-probe configuration Windows itself reads (#593). <see cref="ConfigReadable"/>
/// is false only when the registry key couldn't be opened at all - individual missing values fall
/// back to the same defaults NCSI itself ships with (a documented Windows default, not a guess).</summary>
public sealed record NcsiProbeConfig(
    bool ActiveProbingEnabled, string WebProbeHost, string WebProbePath, string DnsProbeHost, bool ConfigReadable);

/// <summary>#593's full explainer - which of the three checks NCSI itself runs (DNS probe, HTTP/web
/// probe, and the NlaSvc service simply being up) is actually failing right now, live, rather than
/// reading Windows' own cached "No internet, secured" verdict.</summary>
public sealed record NcsiCheckResult(
    NcsiProbeConfig Config, string NlaSvcStateText, bool NlaSvcRunning,
    bool? DnsProbeOk, string? DnsProbeDetail, bool? WebProbeOk, string? WebProbeDetail, string ExplainerText)
{
    /// <summary>Plain "OK"/"Failed"/"Unknown" text for the view - same "compute it once in C#"
    /// tradeoff FirewallProfileStatus.EnabledText/AdapterHealthRow's own text properties already
    /// take, rather than a bool?-to-text converter.</summary>
    public string DnsProbeOkText => DnsProbeOk switch { true => "OK", false => "Failed", null => "Unknown" };
    public string WebProbeOkText => WebProbeOk switch { true => "OK", false => "Failed", null => "Unknown" };
}

/// <summary>
/// Item #593: combines the NCSI (Network Connectivity Status Indicator) active-probe configuration,
/// the NlaSvc service state, and live results of the same DNS-probe/HTTP-probe pair Windows itself
/// runs before deciding "Internet" vs. "No internet, secured" vs. "Limited" - so when Windows' own
/// verdict disagrees with "but my browser works fine", this shows exactly which of the three checks
/// is the one actually failing instead of leaving that as a guess.
///
/// The probe configuration genuinely lives at <see cref="NcsiConfigKeyPath"/> - NlaSvc's own
/// Parameters\Internet subkey - which is where `netsh` and the "Network Connectivity Status
/// Indicator" Group Policy ADMX template both write/read these same values (an internal-only
/// enterprise can point NCSI at a private probe host this way). Falls back to the documented public
/// defaults (msftconnecttest.com/dns.msftncsi.com) when the key or a specific value is absent -
/// that's Windows' own out-of-box behavior, not this app guessing.
///
/// The HTTP probe reuses the exact URL/expected-body pair NetworkDiagnosticsService.CheckCaptivePortalAsync
/// (#51) already established for the existing Connectivity card's captive-portal flag - same request,
/// read here as one of three named checks rather than a single collapsed yes/no. Not shared code
/// (a private HttpClient here, per this class's own remarks) since #51's check is intentionally
/// framed as "captive portal: yes/no/unknown" while this one is framed as "which NCSI check failed" -
/// different question, same underlying request shape.
/// </summary>
public static class NcsiDiagnosticsService
{
    private const string NcsiConfigKeyPath = @"SYSTEM\CurrentControlSet\Services\NlaSvc\Parameters\Internet";

    private const string DefaultWebProbeHost = "www.msftconnecttest.com";
    private const string DefaultWebProbePath = "connecttest.txt";
    private const string DefaultWebProbeExpectedBody = "Microsoft Connect Test";
    private const string DefaultDnsProbeHost = "dns.msftncsi.com";

    private static readonly HttpClient ProbeHttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
    })
    { Timeout = TimeSpan.FromSeconds(4) };

    public static NcsiProbeConfig ReadConfig()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(NcsiConfigKeyPath);
            bool active = key?.GetValue("EnableActiveProbing") is not int e || e != 0; // Windows default is enabled (1); treat "not set" as enabled too
            string webHost = (key?.GetValue("ActiveWebProbeHost") as string)?.Trim() is { Length: > 0 } wh ? wh : DefaultWebProbeHost;
            string webPath = (key?.GetValue("ActiveWebProbePath") as string)?.Trim() is { Length: > 0 } wp ? wp : DefaultWebProbePath;
            string dnsHost = (key?.GetValue("ActiveDnsProbeHost") as string)?.Trim() is { Length: > 0 } dh ? dh : DefaultDnsProbeHost;
            return new NcsiProbeConfig(active, webHost, webPath, dnsHost, key is not null);
        }
        catch
        {
            return new NcsiProbeConfig(true, DefaultWebProbeHost, DefaultWebProbePath, DefaultDnsProbeHost, false);
        }
    }

    public static async Task<NcsiCheckResult> RunAsync()
    {
        var config = ReadConfig();
        var (nlaText, nlaRunning) = ReadNlaSvcState();

        var dnsTask = ProbeDnsAsync(config.DnsProbeHost);
        var webTask = ProbeWebAsync(config.WebProbeHost, config.WebProbePath);
        await Task.WhenAll(dnsTask, webTask);

        var (dnsOk, dnsDetail) = dnsTask.Result;
        var (webOk, webDetail) = webTask.Result;

        string explainer = BuildExplainer(config, nlaRunning, dnsOk, dnsDetail, webOk, webDetail);
        return new NcsiCheckResult(config, nlaText, nlaRunning, dnsOk, dnsDetail, webOk, webDetail, explainer);
    }

    private static string BuildExplainer(NcsiProbeConfig config, bool nlaRunning, bool? dnsOk, string? dnsDetail, bool? webOk, string? webDetail)
    {
        if (!nlaRunning)
            return "Network Location Awareness (NlaSvc) isn't running - Windows can't classify connectivity at all without it, so it will show whatever it last cached (often stale).";

        if (!config.ActiveProbingEnabled)
            return "Active probing is disabled (EnableActiveProbing = 0) - Windows isn't running the DNS/HTTP checks below at all right now, so its connectivity icon reflects a passive guess, not a fresh test.";

        if (webOk == true && dnsOk == true)
            return "Both the DNS probe and the HTTP probe succeeded just now - if Windows is still showing anything other than \"Internet access\", its cached state hasn't caught up yet (it doesn't always re-probe instantly on every change).";

        if (webOk == false && dnsOk == true)
            return $"DNS probe OK, but the HTTP probe failed ({webDetail}) - this is exactly the \"No internet, secured\" pattern: DNS resolves fine (so plenty of ordinary traffic works) but Windows' own connectivity-test URL didn't come back with the exact expected response, so it refuses to call this \"Internet access\". A captive portal, a proxy/firewall rule specific to that one URL, or a security product intercepting HTTP are the usual causes.";

        if (dnsOk == false && webOk == true)
            return $"HTTP probe OK, but the DNS probe failed ({dnsDetail}) - traffic to IP addresses (or anything already cached) can work fine while name resolution for new lookups is broken or slow.";

        if (dnsOk == false && webOk == false)
            return $"Both probes failed (DNS: {dnsDetail}; HTTP: {webDetail}) - Windows' \"No internet\" verdict matches reality; there's no live path out right now.";

        return "Inconclusive - one or both probes couldn't produce a clear result.";
    }

    private static (string StateText, bool Running) ReadNlaSvcState()
    {
        try
        {
            using var sc = new ServiceController("NlaSvc");
            var status = sc.Status;
            return (status.ToString(), status == ServiceControllerStatus.Running);
        }
        catch
        {
            return ("Unknown", false);
        }
    }

    private static async Task<(bool? Ok, string? Detail)> ProbeDnsAsync(string host)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var addresses = await Dns.GetHostAddressesAsync(host, cts.Token);
            return addresses.Length > 0 ? (true, $"{host} resolved ({addresses.Length} address(es))") : (false, $"{host} returned no addresses");
        }
        catch (Exception ex)
        {
            return (false, $"{host} failed to resolve: {ex.Message}");
        }
    }

    private static async Task<(bool? Ok, string? Detail)> ProbeWebAsync(string host, string path)
    {
        string url = $"http://{host.TrimEnd('/')}/{path.TrimStart('/')}";
        try
        {
            using var response = await ProbeHttpClient.GetAsync(url);
            if ((int)response.StatusCode is >= 300 and < 400)
                return (false, $"{url} redirected (HTTP {(int)response.StatusCode}) - looks like a captive portal or interception");
            if (!response.IsSuccessStatusCode)
                return (false, $"{url} returned HTTP {(int)response.StatusCode}");

            string body = (await response.Content.ReadAsStringAsync()).Trim();
            bool matches = body.Equals(DefaultWebProbeExpectedBody, StringComparison.Ordinal);
            return (matches, matches ? $"{url} returned the expected body" : $"{url} returned an unexpected body ({body.Length} chars)");
        }
        catch (Exception ex)
        {
            return (false, $"{url} failed: {ex.Message}");
        }
    }
}
