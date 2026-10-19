using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// suggestions.md #999: a static, hand-maintained catalog of every outbound network call this app
/// can make on its own - the data source for the read-only "Network activity" disclosure page. A
/// plain static table, not something computed at runtime (the task notes are explicit this is a
/// disclosure/documentation feature, not a live network monitor), so it's reviewed and updated by
/// hand whenever a new outbound call is added to this codebase - the same "hand-maintained, not
/// derived" tradeoff RemediationActionCatalog's own action list already takes.
///
/// ----- The Offline-mode boundary (also stated on the disclosure page itself) -----
/// UiPreferences.OfflineMode gates exactly three calls: PublicIpLookupService, UpdateCheckService
/// (including its once-at-startup automatic check), and TracerouteService. It deliberately does
/// NOT gate:
///   - A "Learn more" (#992) or "Read more (offline)" (#993) link/button - opening either hands a
///     URL/file path to the OS's own default browser via ExternalLinkService; the app itself makes
///     no network connection there at all, the browser does, exactly like clicking any other link
///     anywhere in Windows. Also: the offline explainer files are 100% local, so most "Read more"
///     clicks touch no network regardless of this toggle.
///   - NetworkDiagnosticsService's gateway/DNS-resolver/captive-portal checks. The Network tab runs
///     them automatically (once at startup, then every 15s while the app is open - that tab's
///     connectivity panel), and the Troubleshoot tab's "No internet" symptom branch runs them on
///     demand. The entire point of both is to test whether the network works, so gating them
///     behind an "I'm offline" switch would make the tools built to diagnose "no internet" refuse
///     to run while offline. The catalog says so plainly rather than calling them "never automatic".
///   - The Network tab's other diagnostics (speed/bufferbloat tests, latency and DNS-response
///     monitors, hop monitor, jitter and path-MTU tests, the NCSI check, DNS comparisons). Each
///     runs only from its own button or toggle - except the PAC script fetch, which the proxy card
///     also runs once at startup when Windows has a PAC URL configured.
///   - Other tabs' on-demand checks: the symbol-server test (plus the symbol downloads cdb.exe
///     itself makes during dump analysis), the WSUS server reachability check and the Troubleshoot
///     "No internet" TCP connect. Each runs only from its own button, and each exists to test
///     whether that one connection works.
/// </summary>
public static class NetworkActivityCatalogService
{
    public static IReadOnlyList<NetworkActivityEntry> BuildCatalog() => new List<NetworkActivityEntry>
    {
        new()
        {
            Name = "Public IP / ISP lookup",
            Trigger = "Network tab → \"Look up public IP\" button (never automatic)",
            Destination = "https://ipinfo.io/json",
            GatedByOfflineMode = true,
        },
        new()
        {
            Name = "Update check",
            Trigger = "Once automatically at app startup, plus never again this session",
            Destination = "https://api.github.com/repos/MasstarVT/TaskManagerPlus/releases/latest",
            GatedByOfflineMode = true,
        },
        new()
        {
            Name = "Traceroute",
            Trigger = "Network tab → \"Traceroute\" panel, a user-entered host (never automatic)",
            Destination = "The host you type in, via tracert.exe (ICMP, incrementing TTL)",
            GatedByOfflineMode = true,
        },
        new()
        {
            Name = "Connectivity check (gateway/DNS/captive portal)",
            Trigger = "Automatic: once at app startup, then every 15 seconds while the app is open (Network tab's connectivity panel). Also Troubleshoot tab → \"No internet\" symptom",
            Destination = "Your default gateway and 1.1.1.1 (ICMP), a DNS lookup of www.msftconnecttest.com, and http://www.msftconnecttest.com/connecttest.txt",
            GatedByOfflineMode = false,
        },
        new()
        {
            Name = "Speed and bufferbloat tests",
            Trigger = "Network tab → download / upload / bufferbloat test buttons (never automatic)",
            Destination = "The URLs in the test fields (default https://speed.cloudflare.com), plus ICMP to the bufferbloat ping host (default 1.1.1.1)",
            GatedByOfflineMode = false,
        },
        new()
        {
            Name = "Latency and DNS-response monitor",
            Trigger = "Network tab → latency monitor Start toggle; runs until you stop it (never automatic)",
            Destination = "ICMP to this PC's adapter, your gateway, the first hop past it and 1.1.1.1, plus DNS queries to your configured resolvers and 1.1.1.1 / 8.8.8.8 / 9.9.9.9",
            GatedByOfflineMode = false,
        },
        new()
        {
            Name = "Hop monitor, jitter and path MTU tests",
            Trigger = "Network tab → hop monitor Start toggle, jitter test and path MTU buttons (never automatic)",
            Destination = "The host you type in (default 1.1.1.1), via ICMP",
            GatedByOfflineMode = false,
        },
        new()
        {
            Name = "NCSI check",
            Trigger = "Network tab → NCSI check button (never automatic)",
            Destination = "The probe hosts Windows' own connectivity indicator uses (default a DNS lookup of dns.msftncsi.com and http://www.msftconnecttest.com/connecttest.txt)",
            GatedByOfflineMode = false,
        },
        new()
        {
            Name = "DNS resolver comparison and VPN DNS-leak check",
            Trigger = "Network tab → DNS compare and VPN tunnel check buttons (never automatic)",
            Destination = "nslookup queries to your configured resolvers and 1.1.1.1 / 8.8.8.8 / 9.9.9.9, and to your default resolver for www.msftconnecttest.com",
            GatedByOfflineMode = false,
        },
        new()
        {
            Name = "Proxy auto-config (PAC) script fetch",
            Trigger = "Automatic: once at app startup (Network tab's proxy card), plus its Refresh button - only when Windows has a PAC URL configured",
            Destination = "The PAC/auto-config URL configured in Windows' proxy settings",
            GatedByOfflineMode = false,
        },
        new()
        {
            Name = "Other Network tab tests",
            Trigger = "Network tab → \"Resolve names\", dual-stack test and LAN throughput test buttons (never automatic)",
            Destination = "Reverse-DNS lookups of the connections listed, via your resolver; the host name you type in (IPv4 and IPv6 TCP connects); the LAN host or share you enter",
            GatedByOfflineMode = false,
        },
        new()
        {
            Name = "\"No internet\" outbound TCP check",
            Trigger = "Troubleshoot tab → \"No internet\" symptom, after its gateway and DNS steps pass (never automatic)",
            Destination = "A TCP connect to 1.1.1.1 port 443 (no data sent)",
            GatedByOfflineMode = false,
        },
        new()
        {
            Name = "Symbol server test and dump analysis",
            Trigger = "Settings → Crash analysis → \"Test symbol server\" button, and Stability tab → \"Analyse (!analyze -v)\" button (never automatic)",
            Destination = "https://msdl.microsoft.com/download/symbols/ (the test's HTTPS GET); during analysis cdb.exe itself downloads symbols from whatever server its symbol path names",
            GatedByOfflineMode = false,
        },
        new()
        {
            Name = "WSUS server reachability check",
            Trigger = "Windows Health tab → \"Check reachability\" button, shown only when a WUServer update policy is set (never automatic)",
            Destination = "The WUServer URL from Windows Update policy (HTTP HEAD, then GET if HEAD fails)",
            GatedByOfflineMode = false,
        },
        new()
        {
            Name = "\"Learn more\" / \"Read more (offline)\" links",
            Trigger = "Clicking a finding's docs link or offline-explainer button (never automatic)",
            Destination = "Whatever URL the rule specifies (learn.microsoft.com/support.microsoft.com), or a 100% local file - opened in your default browser, not fetched by this app",
            GatedByOfflineMode = false,
        },
    };
}
