using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>One adapter's routing metric (#535), from `netsh interface ipv4/ipv6 show interfaces`.</summary>
public sealed record InterfaceMetricInfo(string AddressFamily, int Index, int Metric, string State, string InterfaceName);

/// <summary>
/// Item #535: parses each adapter's routing metric out of `netsh interface ipv4 show interfaces`
/// (and the ipv6 equivalent) - the same "known tool over raw interop" tradeoff every other
/// netsh/route/sc call in this app already takes, and the same column shape InterfaceMtuService
/// (#511) already parses for `netsh interface ipv6 show subinterfaces` (Idx/Met/MTU/State/Name).
///
/// Windows prefers the *lowest* effective metric among connected interfaces with a default route
/// when deciding which adapter actually carries outbound traffic for a given address family -
/// <see cref="DescribeWinner"/> turns the raw per-adapter metric table into the plain-language
/// "Ethernet is plugged in but everything goes over Wi-Fi" answer by picking the lowest-metric
/// connected interface per family, the classic confusion when a faster/lower-metric Wi-Fi adapter
/// silently wins over a slower/manually-metered wired one.
///
/// On-demand only, alongside the existing Routing card's #513/#514 refresh (this item's own spec
/// places it "next to #513") - it shells out just like that card's own route/persistent-route reads,
/// never a tick.
/// </summary>
public static class InterfaceMetricService
{
    public static async Task<List<InterfaceMetricInfo>> ReadAllAsync()
    {
        var results = new List<InterfaceMetricInfo>();
        results.AddRange(await ReadForFamilyAsync("ipv4"));
        results.AddRange(await ReadForFamilyAsync("ipv6"));
        return results;
    }

    private static async Task<List<InterfaceMetricInfo>> ReadForFamilyAsync(string family)
    {
        var results = new List<InterfaceMetricInfo>();
        try
        {
            var (output, _) = await ToolRunner.RunCapturedAsync("netsh.exe", $"interface {family} show interfaces", 10_000,
                timeoutOutput: string.Empty, includeStderr: false);

            string af = family == "ipv4" ? "IPv4" : "IPv6";
            foreach (var rawLine in output.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;

                // "Idx     Met         MTU          State                Name" - 3 numeric columns
                // (Idx, Met, MTU), a state word, then the interface name (which may itself contain
                // spaces).
                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 5) continue;
                if (!int.TryParse(tokens[0], out int idx)) continue; // header/separator row
                if (!int.TryParse(tokens[1], out int metric)) continue;
                if (!long.TryParse(tokens[2], out _)) continue; // MTU - InterfaceMtuService (#511) already owns that card

                string state = tokens[3];
                string name = string.Join(' ', tokens.Skip(4));
                results.Add(new InterfaceMetricInfo(af, idx, metric, state, name));
            }
        }
        catch
        {
            // Best-effort - return whatever was parsed before the failure.
        }
        return results;
    }

    /// <summary>#535: "which adapter wins" - the lowest-metric *connected* interface per address
    /// family is the one Windows actually prefers for outbound traffic of that family, regardless of
    /// which other adapters are merely up. A tie is reported honestly as a tie rather than guessed at
    /// - Windows' own tie-break in that case isn't something this app claims to predict.</summary>
    public static string DescribeWinner(IReadOnlyList<InterfaceMetricInfo> interfaces)
    {
        var lines = new List<string>();
        foreach (var af in new[] { "IPv4", "IPv6" })
        {
            var connected = interfaces.Where(i => i.AddressFamily == af && i.State.Equals("connected", StringComparison.OrdinalIgnoreCase)).ToList();
            if (connected.Count == 0) continue;

            int minMetric = connected.Min(i => i.Metric);
            var winners = connected.Where(i => i.Metric == minMetric).Select(i => i.InterfaceName).Distinct().ToList();
            var others = connected.Where(i => i.Metric != minMetric).ToList();

            if (winners.Count > 1)
            {
                lines.Add($"{af}: {string.Join(" and ", winners)} are tied at metric {minMetric} - which one actually carries traffic in a tie isn't something this app predicts.");
                continue;
            }

            string line = $"{af}: Windows prefers \"{winners[0]}\" (metric {minMetric}) for outbound traffic.";
            if (others.Count > 0)
            {
                var runnerUp = others.OrderBy(o => o.Metric).First();
                line += $" \"{runnerUp.InterfaceName}\" is connected too, but its higher metric ({runnerUp.Metric}) means it won't carry traffic unless \"{winners[0]}\" goes down - the classic \"it's plugged in but everything still goes over the other adapter\" situation when the numbers land this way.";
            }
            lines.Add(line);
        }
        return lines.Count == 0 ? "No connected interfaces with a metric were found." : string.Join(" ", lines);
    }
}
