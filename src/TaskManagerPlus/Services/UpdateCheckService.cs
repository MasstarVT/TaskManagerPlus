using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 12, #88: read-only "is there a newer release" check against GitHub's Releases API -
/// notify-only, never downloads or installs anything. One GET to
/// api.github.com/repos/MasstarVT/TaskManagerPlus/releases/latest (the actual configured `origin`
/// remote for this repo - see `git remote -v`), compared against this build's own assembly
/// version. GitHub's REST API rejects a request with no User-Agent header outright, so this app's
/// name doubles as that string.
///
/// Deliberately best-effort: no internet connectivity, GitHub's unauthenticated rate limit (60
/// requests/hour per IP - easy to exhaust on a shared network), or any other failure just means
/// no update banner ever appears - never a startup delay or an error dialog, the same "graceful
/// degradation over a blocking/fabricated result" convention PublicIpLookupService and
/// NetworkDiagnosticsService already follow for their own outbound calls.
/// </summary>
public static class UpdateCheckService
{
    private const string ApiUrl = "https://api.github.com/repos/MasstarVT/TaskManagerPlus/releases/latest";
    private static readonly HttpClient Http = BuildClient();

    private static HttpClient BuildClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TaskManagerPlus-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Returns the newer release's tag + page URL when GitHub's "latest" release is
    /// actually newer than this build's assembly version; otherwise (up to date, offline,
    /// rate-limited, or any other failure) returns (null, null) - callers have nothing useful to
    /// do differently for any of those cases beyond "don't show a banner", so they're collapsed
    /// into one result rather than a richer error type nobody would act on.</summary>
    public static async Task<(string? TagName, string? HtmlUrl)> CheckForNewerReleaseAsync()
    {
        try
        {
            using var response = await Http.GetAsync(ApiUrl);
            if (!response.IsSuccessStatusCode) return (null, null);

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            string? url = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return (null, null);

            var latest = ParseVersion(tag);
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            if (latest is null || latest <= current) return (null, null);

            return (tag, url);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>Release tags commonly look like "v1.4.0" or "1.4.0" - strips everything but the
    /// first dotted-number run and parses it as a System.Version, degrading to null (treated as
    /// "no update" by the caller, per CheckForNewerReleaseAsync's remarks) for any tag shape this
    /// doesn't recognize rather than guessing at one.</summary>
    private static Version? ParseVersion(string tag)
    {
        var match = Regex.Match(tag, @"(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?");
        if (!match.Success) return null;

        int major = int.Parse(match.Groups[1].Value);
        int minor = int.Parse(match.Groups[2].Value);
        int build = int.Parse(match.Groups[3].Value);
        int revision = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;
        return new Version(major, minor, build, revision);
    }
}
