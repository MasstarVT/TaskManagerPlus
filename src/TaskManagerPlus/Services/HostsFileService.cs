using System.IO;
using System.Net;

namespace TaskManagerPlus.Services;

/// <summary>One parsed hosts-file mapping (#522). A mutable class rather than a record, same
/// "annotate after the whole file is parsed" shape RoutingTableService.RouteEntry already uses for
/// its own conflict flags - <see cref="IsDuplicate"/>/<see cref="ShadowsRecentLookup"/> can only be
/// known once every line (or the most recently looked-up hostname) has been seen.</summary>
public sealed class HostsFileEntry
{
    public int LineNumber { get; init; }
    public string IpAddress { get; init; } = string.Empty;
    public string Hostname { get; init; } = string.Empty;

    /// <summary>A well-known public domain pointed at 127.0.0.1/0.0.0.0/::1 - the classic
    /// ad-blocker-or-malware shape. Quick flag, not a verdict: plenty of legitimate ad-blocking
    /// hosts files do exactly this on purpose.</summary>
    public bool IsBlackholed { get; init; }

    public bool IsDuplicate { get; set; }
    public bool ShadowsRecentLookup { get; set; }
    public string RawLine { get; init; } = string.Empty;
}

/// <summary>
/// Item #522: parses `%SystemRoot%\System32\drivers\etc\hosts` into a table, beyond the existing
/// #46 "open hosts file in Notepad" shortcut - flags entries that shadow-block a well-known public
/// domain, duplicate hostnames (ambiguous - Windows uses whichever comes first), and any entry
/// that would override the hostname most recently looked up elsewhere on this card (#517's
/// compare, #518's cache search). A plain text-file read, not a recursive filesystem walk, so this
/// runs on load and from a Refresh button rather than needing anything heavier.
/// </summary>
public static class HostsFileService
{
    // A small, curated set of high-traffic public domains worth flagging if blackholed - not
    // remotely exhaustive (an ad-blocker hosts file can run to tens of thousands of lines), just
    // enough to catch "wait, why can't I reach google.com" at a glance. Same "good enough for a
    // quick flag" tradeoff as VpnNameHints/WellKnownDomains-style lists elsewhere in this app.
    private static readonly HashSet<string> WellKnownDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "google.com", "www.google.com", "microsoft.com", "www.microsoft.com", "apple.com",
        "www.apple.com", "amazon.com", "www.amazon.com", "cloudflare.com", "github.com",
        "youtube.com", "www.youtube.com", "facebook.com", "www.facebook.com", "x.com",
        "twitter.com", "wikipedia.org", "en.wikipedia.org", "office.com", "live.com",
        "outlook.com", "windowsupdate.com",
    };

    private static readonly string[] BlackholeAddresses = { "127.0.0.1", "0.0.0.0", "::1" };

    public static string HostsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

    /// <summary><paramref name="recentLookupHostname"/> is whatever hostname the user most
    /// recently ran a lookup against elsewhere on the DNS card (the #517 compare box) - null/empty
    /// simply means no entry gets flagged as shadowing a lookup.</summary>
    public static List<HostsFileEntry> Parse(string? recentLookupHostname = null)
    {
        var entries = new List<HostsFileEntry>();
        try
        {
            string path = HostsFilePath;
            if (!File.Exists(path)) return entries;

            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                int hashIdx = line.IndexOf('#');
                if (hashIdx >= 0) line = line[..hashIdx].Trim();
                if (line.Length == 0) continue;

                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2 || !IPAddress.TryParse(tokens[0], out var ip)) continue;
                string ipText = ip.ToString();

                foreach (var host in tokens.Skip(1))
                {
                    bool blackholed = BlackholeAddresses.Contains(ipText) && WellKnownDomains.Contains(host);
                    bool shadowsLookup = !string.IsNullOrWhiteSpace(recentLookupHostname) &&
                        host.Equals(recentLookupHostname.Trim(), StringComparison.OrdinalIgnoreCase);

                    entries.Add(new HostsFileEntry
                    {
                        LineNumber = i + 1,
                        IpAddress = ipText,
                        Hostname = host,
                        IsBlackholed = blackholed,
                        ShadowsRecentLookup = shadowsLookup,
                        RawLine = raw.Trim(),
                    });
                }
            }

            foreach (var group in entries.GroupBy(e => e.Hostname, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() <= 1) continue;
                foreach (var e in group) e.IsDuplicate = true;
            }
        }
        catch
        {
            // Denied/missing file - empty list just means nothing to show.
        }
        return entries;
    }
}
