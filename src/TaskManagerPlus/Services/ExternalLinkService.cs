using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>
/// suggestions.md #992/#993: the one place a "Learn more" (online docs) or "Read more (offline)"
/// (a local explainer HTML file) button opens something in the user's own default browser -
/// previously every such call site in this app (EvidenceBundleViewModel's "reveal in Explorer",
/// MainViewModel's update-check URL, NetworkViewModel's hosts-file edit, RestorePointService's
/// System Protection panel) just inlined its own `Process.Start(..., UseShellExecute = true)`
/// rather than sharing one helper - centralized here for the two new call sites this domain adds,
/// not a retrofit of every pre-existing one.
///
/// #999: deliberately NOT gated by UiPreferences.OfflineMode. Opening a link is a direct,
/// user-initiated click handing a URL (or a local file path) to the OS's own browser - the app
/// itself makes no network call here at all, unlike PublicIpLookupService/UpdateCheckService/
/// TracerouteService which each open their own HTTP/ICMP connection with no user action in
/// between. See NetworkActivityCatalogService's remarks for the full write-up of this boundary,
/// which is also stated on the in-app "Network activity" disclosure page.
/// </summary>
public static class ExternalLinkService
{
    /// <summary>Opens a URL or local file path in the OS's default handler. Best-effort - a
    /// missing browser association or a malformed URL just means nothing happens, not a crash.</summary>
    public static bool TryOpen(string urlOrPath)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(urlOrPath) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
