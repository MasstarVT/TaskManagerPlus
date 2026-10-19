using System.IO;

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
    /// <summary>Opens a URL or local file path with the user's default handler, unelevated (through
    /// SafeLaunchService - an elevated ShellExecute resolves the browser through HKCU associations
    /// any unelevated process can rewrite). Only an absolute http/https URL or an existing local
    /// .html/.htm file is accepted: DocsUrl comes from user-writable rule packs. False when nothing
    /// was opened.</summary>
    public static bool TryOpen(string urlOrPath)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath)) return false;
        if (IsWebUrl(urlOrPath)) return SafeLaunchService.OpenUrl(urlOrPath);
        return IsLocalHtmlFile(urlOrPath) && SafeLaunchService.OpenDocument(urlOrPath);
    }

    /// <summary>Opens an http/https URL unelevated; when that isn't possible, copies it to the
    /// clipboard instead. Null when the browser was asked to open it, otherwise the text to show
    /// the user.</summary>
    public static string? OpenUrlOrCopy(string url)
    {
        if (IsWebUrl(url) && SafeLaunchService.OpenUrl(url)) return null;
        try
        {
            System.Windows.Clipboard.SetText(url);
            return $"Couldn't open your browser, so the link was copied to the clipboard: {url}";
        }
        catch
        {
            return $"Couldn't open your browser. The link is: {url}";
        }
    }

    private static bool IsWebUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsLocalHtmlFile(string s)
    {
        try
        {
            if (!Path.IsPathFullyQualified(s)) return false;
            string ext = Path.GetExtension(s);
            return (ext.Equals(".html", StringComparison.OrdinalIgnoreCase) || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase))
                && File.Exists(s);
        }
        catch
        {
            return false;
        }
    }
}
