using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>#572's PAC fetch/health result. <see cref="Attempted"/> is false when there was no
/// AutoConfigUrl to fetch at all (distinct from an attempt that failed). <see cref="IsSlow"/> flags
/// the multi-second-hang-before-every-new-connection symptom a slow/unreachable PAC server
/// causes.</summary>
public sealed record PacFetchResult(bool Attempted, bool Success, long? ElapsedMs, string? Body, string? ErrorMessage, bool IsSlow);

/// <summary>#573's per-user-vs-system-wide comparison result.</summary>
public sealed record ProxyDivergenceInfo(string PerUserSummary, string SystemWideSummary, bool Mismatch, string? MismatchReason);

/// <summary>
/// Items #572/#573/#574 (suggestions.md "Proxy, PAC, VPN and Winsock"): extends the existing
/// read-only proxy readout (NetworkDiagnosticsService.ReadProxyConfig, #47) with a PAC-file health
/// check, a per-user-vs-machine-wide divergence check, and bypass-list parsing/testing - all display
/// only, this app never writes to any of these settings.
/// </summary>
public static class ProxyDiagnosticsService
{
    private const long SlowPacThresholdMs = 2000;

    // UseProxy = false: the PAC URL itself must be fetched directly, never through the proxy the
    // PAC script it returns would go on to configure (that would usually just fail outright, since
    // the proxy isn't reachable/known until the PAC script is read).
    private static readonly HttpClient PacHttpClient = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = true })
    { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>#572: fetches and times the PAC script named by AutoConfigURL, flagging a slow or
    /// unreachable server - the characteristic multi-second hang before every new connection this
    /// item's own text describes. `file:` URLs (a PAC script can be configured as a local file, not
    /// just an HTTP URL) are read directly rather than through HttpClient.</summary>
    public static async Task<PacFetchResult> FetchPacAsync(string autoConfigUrl)
    {
        if (string.IsNullOrWhiteSpace(autoConfigUrl))
            return new PacFetchResult(false, false, null, null, null, false);

        var sw = Stopwatch.StartNew();
        try
        {
            string body;
            if (autoConfigUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                string localPath = new Uri(autoConfigUrl).LocalPath;
                body = await File.ReadAllTextAsync(localPath);
            }
            else
            {
                using var response = await PacHttpClient.GetAsync(autoConfigUrl);
                response.EnsureSuccessStatusCode();
                body = await response.Content.ReadAsStringAsync();
            }
            sw.Stop();
            return new PacFetchResult(true, true, sw.ElapsedMilliseconds, body, null, sw.ElapsedMilliseconds >= SlowPacThresholdMs);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new PacFetchResult(true, false, sw.ElapsedMilliseconds, null, ex.Message, false);
        }
    }

    /// <summary>#573: compares the per-user WinHTTP/IE proxy this app already reads (HKCU Internet
    /// Settings) against the machine-wide WinHTTP proxy `netsh winhttp show proxy` reports - the
    /// reason a browser (which honors the per-user setting) can work while Windows Update, the
    /// Store, and most Windows services (which use the separate machine-wide WinHTTP proxy) all
    /// fail, or vice versa.</summary>
    public static async Task<ProxyDivergenceInfo> ReadDivergenceAsync(ProxyConfigInfo perUser)
    {
        string output = await RunNetshAsync("winhttp show proxy");
        var (systemEnabled, systemServer) = ParseWinHttpProxy(output);

        string userSummary = perUser.Enabled
            ? (perUser.ProxyServer.Length > 0 ? perUser.ProxyServer : "Enabled, no server configured")
            : "Direct (no proxy)";
        string systemSummary = systemEnabled ? systemServer : "Direct (no proxy)";

        bool mismatch = perUser.Enabled != systemEnabled ||
            (perUser.Enabled && systemEnabled && !Normalize(perUser.ProxyServer).Equals(Normalize(systemServer), StringComparison.OrdinalIgnoreCase));

        string? reason = !mismatch ? null :
            perUser.Enabled && !systemEnabled
                ? "Your account has a proxy configured, but the machine-wide WinHTTP proxy is Direct - Windows Update, the Microsoft Store, and most Windows services (which use WinHTTP, not your per-user setting) will bypass your proxy entirely."
                : !perUser.Enabled && systemEnabled
                    ? "The machine-wide WinHTTP proxy is configured, but your own per-user proxy is Direct - most desktop apps/browsers (which honor the per-user setting) won't use it, while Windows Update/the Store/services will."
                    : "Your per-user proxy server and the machine-wide WinHTTP proxy server are different - traffic takes two different paths depending on which setting a given app honors.";

        return new ProxyDivergenceInfo(userSummary, systemSummary, mismatch, reason);
    }

    private static string Normalize(string s) => s.Trim().TrimEnd('/');

    private static (bool Enabled, string Server) ParseWinHttpProxy(string output)
    {
        if (output.Contains("Direct access", StringComparison.OrdinalIgnoreCase)) return (false, string.Empty);

        var match = Regex.Match(output, @"Proxy Server\(s\)\s*:\s*(\S+)");
        return match.Success ? (true, match.Groups[1].Value.Trim()) : (false, string.Empty);
    }

    /// <summary>#574: splits ProxyOverride into individual bypass entries.</summary>
    public static List<string> ParseBypassList(string proxyOverride)
    {
        if (string.IsNullOrWhiteSpace(proxyOverride)) return new();
        return proxyOverride
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>#574: "does this host bypass the proxy" - re-implements the classic WinINet/WinHTTP
    /// bypass-matching rule in managed code (a leading/trailing '*' or '?' wildcard per entry, plus
    /// the special literal "&lt;local&gt;" matching any hostname with no dots) rather than calling
    /// into wininet.dll, since this app has no wininet interop anywhere else to build on and the
    /// matching rule itself is small and well documented.</summary>
    public static bool TestBypasses(string proxyOverride, string hostname)
    {
        hostname = hostname.Trim();
        if (hostname.Length == 0) return false;

        var entries = ParseBypassList(proxyOverride);

        bool hasLocalEntry = entries.Any(e => e.Equals("<local>", StringComparison.OrdinalIgnoreCase));
        if (hasLocalEntry && !hostname.Contains('.') && !System.Net.IPAddress.TryParse(hostname, out _))
            return true;

        foreach (var entry in entries)
        {
            if (entry.Equals("<local>", StringComparison.OrdinalIgnoreCase)) continue;
            if (WildcardMatch(entry, hostname)) return true;
        }
        return false;
    }

    private static bool WildcardMatch(string pattern, string input)
    {
        string regexPattern = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }

    private static async Task<string> RunNetshAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return string.Empty;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(15000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return string.Empty;
            }

            return (await outputTask) + (await errorTask);
        }
        catch
        {
            return string.Empty;
        }
    }
}
