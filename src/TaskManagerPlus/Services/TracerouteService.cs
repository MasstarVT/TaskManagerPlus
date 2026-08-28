using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>One parsed hop out of a `tracert -d` run (#516) - <see cref="Ip"/> is null when the
/// hop timed out ("Request timed out." in tracert's own output), kept as a distinct hop rather
/// than dropped so a baseline diff can tell "this hop still doesn't reply" apart from "this hop
/// disappeared".</summary>
public sealed record TracerouteHop(int HopNumber, string? Ip);

/// <summary>
/// On-demand traceroute (round 9, #49) to a user-entered host. Shells out to tracert.exe rather
/// than re-implementing ICMP TTL-stepping from scratch - the same "known Windows tool, not raw
/// interop" tradeoff defrag.exe/schtasks.exe/sc.exe/vssadmin.exe already take elsewhere in this
/// app, and tracert's own text output is a stable, long-standing contract. A basic hostname-shape
/// check is applied before shelling out at all, since this runs on an arbitrary user-typed string.
/// </summary>
public static class TracerouteService
{
    private static readonly Regex ValidHostRegex = new(@"^[A-Za-z0-9][A-Za-z0-9.\-:]*$", RegexOptions.Compiled);

    // Matches a leading hop number (tracert -d's numeric-only lines) and, separately, an IPv4 or
    // bare IPv6 address at the end of the line - tracert -d's own three timing columns sit
    // between the two, so this doesn't try to parse them.
    private static readonly Regex HopNumberRegex = new(@"^\s*(\d{1,2})\s", RegexOptions.Compiled);
    private static readonly Regex TrailingAddressRegex = new(@"(\d{1,3}(?:\.\d{1,3}){3}|[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{0,4}){2,7})\s*$", RegexOptions.Compiled);

    /// <summary>#516: parses a completed tracert -d run's raw text output into structured hops,
    /// for TracerouteBaselineService's save/diff to work against instead of the raw text. Only
    /// meaningful for -d (numeric) output, which is all RunAsync ever produces - a reverse-DNS
    /// hostname column would need different parsing this app doesn't request.</summary>
    public static List<TracerouteHop> ParseHops(string tracerouteOutput)
    {
        var hops = new List<TracerouteHop>();
        if (string.IsNullOrWhiteSpace(tracerouteOutput)) return hops;

        foreach (var rawLine in tracerouteOutput.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            var hopMatch = HopNumberRegex.Match(line);
            if (!hopMatch.Success) continue;
            int hopNumber = int.Parse(hopMatch.Groups[1].Value);

            if (line.Contains("Request timed out", StringComparison.OrdinalIgnoreCase))
            {
                hops.Add(new TracerouteHop(hopNumber, null));
                continue;
            }

            var addressMatch = TrailingAddressRegex.Match(line);
            hops.Add(new TracerouteHop(hopNumber, addressMatch.Success ? addressMatch.Value : null));
        }
        return hops;
    }

    public static async Task<string> RunAsync(string host, CancellationToken cancellationToken = default)
    {
        // #999: Offline mode hard-disables this call - see NetworkActivityCatalogService's remarks
        // for the boundary.
        if (UiPreferencesService.Load().OfflineMode)
            return "Offline mode is on (Settings) - network lookups are disabled. Turn it off to run a traceroute.";

        host = host.Trim();
        if (host.Length == 0) return "Enter a host name or IP address first.";
        if (host.Length > 255 || !ValidHostRegex.IsMatch(host)) return "That doesn't look like a valid host name or IP address.";

        try
        {
            var psi = new ProcessStartInfo("tracert.exe", $"-d -h 20 -w 1000 {host}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "Couldn't start tracert.exe.";

            var outputTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = proc.StandardError.ReadToEndAsync(cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return "Traceroute timed out after 30 seconds (a hop count of 20 with a 1s-per-hop timeout can still run long on a lossy path).";
            }

            string output = (await outputTask) + (await errorTask);
            return string.IsNullOrWhiteSpace(output) ? "No output." : output.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Traceroute failed: {ex.Message}";
        }
    }
}
