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
/// UiPreferences.OfflineMode gates exactly the calls this app makes silently on its own with no
/// further click in between: PublicIpLookupService, UpdateCheckService (including its once-at-
/// startup automatic check), and TracerouteService. It deliberately does NOT gate:
///   - A "Learn more" (#992) or "Read more (offline)" (#993) link/button - opening either hands a
///     URL/file path to the OS's own default browser via ExternalLinkService; the app itself makes
///     no network connection there at all, the browser does, exactly like clicking any other link
///     anywhere in Windows. Also: the offline explainer files are 100% local, so most "Read more"
///     clicks touch no network regardless of this toggle.
///   - NetworkDiagnosticsService's gateway/DNS-resolver/captive-portal checks, which power the
///     Troubleshoot tab's "No internet" symptom branch - the entire point of that branch is to test
///     whether the network works, so gating it behind an "I'm offline" switch would make the one
///     tool built to diagnose "no internet" refuse to run while offline. It's still exactly as
///     on-demand/user-triggered as everything else here (only runs when that symptom is picked).
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
            Trigger = "Troubleshoot tab → \"No internet\" symptom (never automatic)",
            Destination = "Your default gateway, 1.1.1.1 (ICMP), and http://www.msftconnecttest.com/connecttest.txt",
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
