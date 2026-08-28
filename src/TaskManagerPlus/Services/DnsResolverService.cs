using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>One adapter's configured resolver IPs (#519, top section) - plain
/// <see cref="NetworkInterface"/> enumeration, no I/O, so this is cheap to call on every refresh.</summary>
public sealed record AdapterDnsResolverInfo(string AdapterName, List<string> ResolverIps);

/// <summary>One resolver's answer to a single #517/#519 comparison run - <see cref="IsConfiguredResolver"/>/
/// <see cref="AdapterName"/> are set only for a resolver that came from an adapter's own DNS
/// configuration (the fixed 1.1.1.1/8.8.8.8/9.9.9.9 probes always have <see cref="AdapterName"/>
/// empty), so the view can tell "your own resolver" apart from "a public one we added for
/// comparison".</summary>
public sealed record DnsResolverAnswer(
    string ResolverIp, bool IsConfiguredResolver, string AdapterName,
    bool Success, List<string> Answers, long ElapsedMs, string? Error);

/// <summary>One completed #517/#519 comparison run. <see cref="AnswersDiverge"/> is true when two
/// or more resolvers that both succeeded returned different answer sets - "quick flag, not a
/// verdict": a divergent answer can mean a hijacking/filtering resolver, a stale cache, or
/// perfectly normal GeoDNS/CDN load-balancing, and this app has no way to tell those apart from
/// the outside. <see cref="FirstRespondingConfiguredResolver"/> is #519's "who actually answered" -
/// the fastest successful reply among only the OS-configured resolvers (never the fixed public
/// ones added purely for comparison), or null if none of them answered at all.</summary>
public sealed record DnsCompareResult(
    string Hostname, DateTime RanUtc, List<DnsResolverAnswer> Resolvers,
    bool AnswersDiverge, string? FirstRespondingConfiguredResolver, string? ValidationError);

/// <summary>
/// Items #517/#519 (suggestions.md "DNS resolution, cache and configuration"): resolves one
/// hostname against every adapter's configured resolvers plus three fixed public resolvers
/// (1.1.1.1, 8.8.8.8, 9.9.9.9), side by side - divergent answers point at a hijacking/filtering
/// resolver or a stale cache, and a VPN/virtual adapter's resolver silently winning shows up
/// directly as "which one answered first".
///
/// Shells out to nslookup.exe per resolver (the same "known Windows tool over raw interop"
/// tradeoff every other netsh/sc/tracert call in this app already takes) rather than hand-rolling
/// a raw UDP/53 query client here - a one-shot, user-triggered comparison of a handful of
/// resolvers doesn't have the "runs forever, every few seconds" cost profile that makes a raw
/// socket meaningfully simpler (contrast DnsResponseTimeMonitorService's #526 continuous chart,
/// which does use a raw UDP query for exactly that reason). nslookup's field labels ("Name:",
/// "Addresses:", "can't find") are English-locale text, same documented limitation
/// WifiDiagnosticsService's netsh parse already carries - this silently returns an empty answer
/// set on a non-English Windows install rather than misparsing.
/// </summary>
public static class DnsResolverService
{
    public static readonly string[] FixedPublicResolvers = { "1.1.1.1", "8.8.8.8", "9.9.9.9" };

    private const int NslookupTimeoutMs = 4000;
    private static readonly Regex ValidHostRegex = new(@"^[A-Za-z0-9][A-Za-z0-9.\-]*$", RegexOptions.Compiled);

    /// <summary>#519 top section: every active adapter's own configured resolver IPs - no test
    /// resolution here, just what Windows itself has configured.</summary>
    public static List<AdapterDnsResolverInfo> ReadConfiguredResolvers()
    {
        var list = new List<AdapterDnsResolverInfo>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var ips = ni.GetIPProperties().DnsAddresses
                    .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Select(a => a.ToString())
                    .ToList();
                if (ips.Count > 0) list.Add(new AdapterDnsResolverInfo(ni.Name, ips));
            }
        }
        catch
        {
            // Best-effort - empty list just means nothing to show.
        }
        return list;
    }

    /// <summary>#517/#519: resolves <paramref name="hostname"/> against every configured resolver
    /// found above plus the three fixed public ones, in parallel, timing each and comparing the
    /// resulting answer sets.</summary>
    public static async Task<DnsCompareResult> CompareAsync(string hostname)
    {
        hostname = hostname.Trim();
        if (hostname.Length == 0 || hostname.Length > 255 || !ValidHostRegex.IsMatch(hostname))
            return new DnsCompareResult(hostname, DateTime.UtcNow, new(), false, null,
                "That doesn't look like a valid host name.");

        var adapterResolvers = ReadConfiguredResolvers();
        var adapterByIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in adapterResolvers)
            foreach (var ip in a.ResolverIps)
                if (!adapterByIp.ContainsKey(ip)) adapterByIp[ip] = a.AdapterName;

        var allIps = adapterByIp.Keys
            .Concat(FixedPublicResolvers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tasks = allIps.Select(ip => QueryOneAsync(
            hostname, ip, adapterByIp.TryGetValue(ip, out var name) ? name : string.Empty, adapterByIp.ContainsKey(ip)));
        var results = (await Task.WhenAll(tasks)).ToList();

        var successfulAnswerSets = results
            .Where(r => r.Success)
            .Select(r => string.Join(",", r.Answers.OrderBy(a => a, StringComparer.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();
        bool diverge = successfulAnswerSets.Count > 1;

        string? firstResponder = results
            .Where(r => r.IsConfiguredResolver && r.Success)
            .OrderBy(r => r.ElapsedMs)
            .Select(r => r.ResolverIp)
            .FirstOrDefault();

        return new DnsCompareResult(hostname, DateTime.UtcNow, results, diverge, firstResponder, null);
    }

    private static async Task<DnsResolverAnswer> QueryOneAsync(string hostname, string resolverIp, string adapterName, bool isConfigured)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var psi = new ProcessStartInfo("nslookup.exe", $"{hostname} {resolverIp}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return new DnsResolverAnswer(resolverIp, isConfigured, adapterName, false, new(), 0, "Couldn't start nslookup.exe");

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(NslookupTimeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                sw.Stop();
                return new DnsResolverAnswer(resolverIp, isConfigured, adapterName, false, new(), sw.ElapsedMilliseconds, "Timed out");
            }

            string output = (await outputTask) + (await errorTask);
            sw.Stop();

            var (answers, error) = ParseNslookupOutput(output);
            bool success = answers.Count > 0;
            return new DnsResolverAnswer(resolverIp, isConfigured, adapterName, success, answers, sw.ElapsedMilliseconds,
                success ? null : (error ?? "No records returned"));
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DnsResolverAnswer(resolverIp, isConfigured, adapterName, false, new(), sw.ElapsedMilliseconds, ex.Message);
        }
    }

    /// <summary>Splits nslookup's own "Server:/Address: <the resolver>" header block from the
    /// actual answer block (separated by a blank line), then pulls every IP-shaped whitespace
    /// token out of the answer block - sidesteps needing to match the "Address:" vs "Addresses:"
    /// label nslookup uses depending on record count, since a bare label token never itself parses
    /// as an IP address.</summary>
    private static (List<string> Answers, string? Error) ParseNslookupOutput(string output)
    {
        string normalized = output.Replace("\r\n", "\n");
        var blocks = Regex.Split(normalized, @"\n\s*\n");
        string answerSection = blocks.Length > 1 ? string.Join("\n", blocks.Skip(1)) : normalized;

        bool looksLikeFailure =
            answerSection.Contains("can't find", StringComparison.OrdinalIgnoreCase) ||
            answerSection.Contains("Non-existent domain", StringComparison.OrdinalIgnoreCase) ||
            answerSection.Contains("No response from server", StringComparison.OrdinalIgnoreCase) ||
            answerSection.Contains("request timed out", StringComparison.OrdinalIgnoreCase) ||
            answerSection.Contains("server failed", StringComparison.OrdinalIgnoreCase);

        var answers = new List<string>();
        foreach (var rawLine in answerSection.Split('\n'))
        {
            foreach (var token in rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = token.TrimEnd(',');
                if (IPAddress.TryParse(trimmed, out var ip)) answers.Add(ip.ToString());
            }
        }

        string? error = looksLikeFailure ? answerSection.Trim() : null;
        return (answers.Distinct().ToList(), error);
    }
}
