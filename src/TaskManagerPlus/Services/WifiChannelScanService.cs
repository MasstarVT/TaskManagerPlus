using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>One BSSID row from a `netsh wlan show networks mode=bssid` scan (#538/#546).</summary>
public sealed record WifiScanNetwork(string Ssid, string Bssid, int? SignalPercent, string RadioType, string Band, int? Channel, bool IsCurrentBssid);

/// <summary>Per-channel occupancy for the #538 congestion chart - AP count and summed signal, the
/// same two figures every consumer Wi-Fi-analyzer channel chart plots.</summary>
public sealed record WifiChannelOccupancy(string Band, int Channel, int ApCount, int SummedSignalPercent);

/// <summary>#546: every BSSID sharing one SSID, strongest signal first - the "mesh/extender" view.</summary>
public sealed record WifiSsidGroup(string Ssid, List<WifiScanNetwork> Bssids);

/// <summary>#538's full scan result.</summary>
public sealed record WifiScanResult(List<WifiScanNetwork> Networks, List<WifiChannelOccupancy> Occupancy, DateTime ScannedAtUtc);

/// <summary>
/// Item #538 (plus #539/#546, which are both read off the same scan rather than a second one):
/// parses `netsh wlan show networks mode=bssid` into a per-BSSID list, then derives per-channel
/// occupancy, a 2.4 GHz overlap verdict, and a same-SSID BSSID grouping from it.
///
/// This is exactly the kind of scan CLAUDE.md's on-demand convention exists for: `netsh wlan show
/// networks` makes the adapter actively probe every channel it can reach, which briefly interrupts
/// the current association's own data flow - the same reason a phone's Wi-Fi analyzer app shows a
/// spinner instead of running continuously. Callers gate this behind an explicit "Scan" button,
/// never a timer, and never call it from the same loop WifiSignalMonitorService's continuous #537
/// sampling uses (that one deliberately avoids anything that could trigger a scan).
/// </summary>
public static class WifiChannelScanService
{
    private const int TimeoutMs = 10000; // an active multi-band scan can genuinely take several seconds

    public static async Task<WifiScanResult> ScanAsync(string? connectedBssid)
    {
        string output = await RunNetshAsync("wlan show networks mode=bssid");
        var networks = ParseNetworks(output, connectedBssid);
        var occupancy = ComputeOccupancy(networks);
        return new WifiScanResult(networks, occupancy, DateTime.UtcNow);
    }

    /// <summary>#538: AP count and summed signal per (band, channel) - the two series every
    /// consumer Wi-Fi-analyzer channel chart plots.</summary>
    public static List<WifiChannelOccupancy> ComputeOccupancy(List<WifiScanNetwork> networks) =>
        networks
            .Where(n => n.Channel is not null)
            .GroupBy(n => (n.Band, Channel: n.Channel!.Value))
            .Select(g => new WifiChannelOccupancy(g.Key.Band, g.Key.Channel, g.Count(), g.Sum(n => n.SignalPercent ?? 0)))
            .OrderBy(o => o.Band, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.Channel)
            .ToList();

    /// <summary>#538: "recommend the least-congested channel" - for 2.4 GHz that's specifically
    /// among the non-overlapping 1/6/11 set (anything else overlaps a neighbour by definition, see
    /// #539); for 5/6 GHz there's no fixed small candidate set the way 2.4 GHz has (the usable list
    /// varies by region/DFS availability), so this recommends the least-busy channel actually seen
    /// in this scan rather than guessing at channels nothing was observed on.</summary>
    public static string RecommendChannelText(List<WifiChannelOccupancy> occupancy, string band)
    {
        var bandChannels = occupancy.Where(o => o.Band == band).ToList();

        if (band == "2.4 GHz")
        {
            var candidates = new[] { 1, 6, 11 };
            var best = candidates
                .Select(c => bandChannels.FirstOrDefault(o => o.Channel == c) ?? new WifiChannelOccupancy(band, c, 0, 0))
                .OrderBy(o => o.ApCount).ThenBy(o => o.SummedSignalPercent).ThenBy(o => o.Channel)
                .First();
            return best.ApCount == 0
                ? $"Channel {best.Channel} is clear of the non-overlapping 1/6/11 set (no other networks seen there)."
                : $"Least congested of the non-overlapping 1/6/11 set: channel {best.Channel} ({best.ApCount} network(s) seen, summed signal {best.SummedSignalPercent}%).";
        }

        if (bandChannels.Count == 0) return $"No {band} networks observed in this scan.";
        var leastBusy = bandChannels.OrderBy(o => o.ApCount).ThenBy(o => o.SummedSignalPercent).ThenBy(o => o.Channel).First();
        return $"Least busy channel observed: {leastBusy.Channel} ({leastBusy.ApCount} network(s), summed signal {leastBusy.SummedSignalPercent}%). "
             + "Only channels seen in this scan are considered - an empty channel nothing broadcast on won't show up here.";
    }

    /// <summary>#539: on top of the #538 scan, scores how many neighbouring 2.4 GHz networks
    /// overlap the channel this machine is actually on right now - a neighbour on channel 3 hurts
    /// both 1 and 6 even though it's on neither, since 2.4 GHz's 22 MHz-wide channels only stop
    /// overlapping 4-5 numbers apart. Quick flag, not a verdict: it counts contention, not measured
    /// throughput impact.</summary>
    public static string? ComputeOverlapVerdict(List<WifiScanNetwork> networks, int? myChannel, string? myBssid)
    {
        if (myChannel is null || myChannel is < 1 or > 14) return null;

        var neighbours = networks
            .Where(n => n.Band == "2.4 GHz" && n.Channel is not null
                        && !string.Equals(n.Bssid, myBssid, StringComparison.OrdinalIgnoreCase)
                        && Math.Abs(n.Channel.Value - myChannel.Value) is > 0 and <= 4)
            .ToList();

        if (neighbours.Count == 0)
            return $"Your channel ({myChannel}) has no overlapping neighbours in this scan.";

        var examples = neighbours.Select(n => $"{(string.IsNullOrEmpty(n.Ssid) ? "(hidden)" : n.Ssid)} on ch.{n.Channel}")
            .Distinct().Take(4).ToList();
        return $"Your channel ({myChannel}) overlaps {neighbours.Count} neighbouring network(s): {string.Join(", ", examples)}"
             + (neighbours.Count > examples.Count ? ", ..." : "")
             + ". Each one competes for the same airtime even though it isn't on your exact channel. Quick flag, not a verdict.";
    }

    /// <summary>#546: every BSSID sharing one SSID, strongest first - multi-BSSID groups (mesh/
    /// extender setups) sorted to the top since that's the case "sticky client" behaviour matters
    /// for.</summary>
    public static List<WifiSsidGroup> GroupBySsid(List<WifiScanNetwork> networks) =>
        networks
            .GroupBy(n => n.Ssid, StringComparer.OrdinalIgnoreCase)
            .Select(g => new WifiSsidGroup(
                string.IsNullOrEmpty(g.Key) ? "(hidden)" : g.Key,
                g.OrderByDescending(n => n.SignalPercent ?? -1).ToList()))
            .OrderByDescending(g => g.Bssids.Count)
            .ThenBy(g => g.Ssid, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ---- netsh output parsing ----------------------------------------------------------------

    internal static List<WifiScanNetwork> ParseNetworks(string output, string? connectedBssid)
    {
        var results = new List<WifiScanNetwork>();
        string? currentSsid = null;
        string? currentBssid = null;
        int? signal = null;
        string? radioType = null;
        string? band = null;
        int? channel = null;

        void Flush()
        {
            if (currentBssid is null) return;
            string resolvedBand = band ?? InferBand(channel, radioType);
            bool isCurrent = connectedBssid is not null && string.Equals(currentBssid, connectedBssid, StringComparison.OrdinalIgnoreCase);
            results.Add(new WifiScanNetwork(currentSsid ?? string.Empty, currentBssid, signal, radioType ?? string.Empty, resolvedBand, channel, isCurrent));
            currentBssid = null; signal = null; radioType = null; band = null; channel = null;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("SSID ", StringComparison.Ordinal) && trimmed.Contains(':'))
            {
                Flush();
                currentSsid = AfterColon(trimmed);
                continue;
            }
            if (trimmed.StartsWith("BSSID ", StringComparison.Ordinal) && trimmed.Contains(':'))
            {
                Flush();
                currentBssid = AfterColon(trimmed);
                continue;
            }
            if (currentBssid is null) continue; // not inside a BSSID block yet - ignore header/SSID-level fields

            int colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0) continue;
            string label = trimmed[..colonIdx].Trim();
            string value = trimmed[(colonIdx + 1)..].Trim();

            if (label.Equals("Signal", StringComparison.OrdinalIgnoreCase) && int.TryParse(value.TrimEnd('%'), out var s))
                signal = s;
            else if (label.Equals("Radio type", StringComparison.OrdinalIgnoreCase))
                radioType = value;
            else if (label.Equals("Band", StringComparison.OrdinalIgnoreCase))
                band = value;
            else if (label.Equals("Channel", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var c))
                channel = c;
        }
        Flush();
        return results;
    }

    private static string AfterColon(string s)
    {
        int idx = s.IndexOf(':');
        return idx < 0 ? string.Empty : s[(idx + 1)..].Trim();
    }

    /// <summary>Only used when netsh's own output doesn't include a "Band" line (older Windows
    /// builds) - channel 1-14 is unambiguously 2.4 GHz, and 36-177 is unambiguously 5 GHz. 6 GHz
    /// channel numbers overlap 5 GHz's numbering space, so this is a best-effort heuristic (ax/be
    /// radio + a channel number 6 GHz commonly uses), not authoritative - current Windows builds
    /// supply "Band" directly, which is used in preference to this whenever present.</summary>
    private static string InferBand(int? channel, string? radioType)
    {
        if (channel is null) return "Unknown";
        if (channel is >= 1 and <= 14) return "2.4 GHz";
        if (channel is >= 36 and <= 177) return "5 GHz";
        bool likelyAxOrBe = radioType is not null &&
            (radioType.Contains("ax", StringComparison.OrdinalIgnoreCase) || radioType.Contains("be", StringComparison.OrdinalIgnoreCase));
        if (likelyAxOrBe && channel is >= 1 and <= 233) return "6 GHz";
        return "Unknown";
    }

    /// <summary>Thin adapter over the shared ToolRunner (#1084) - kept separate from
    /// WifiDiagnosticsService's own call since this scan needs a much longer timeout (an active
    /// multi-band scan takes longer than a plain `show interfaces` read).</summary>
    private static async Task<string> RunNetshAsync(string arguments)
    {
        try
        {
            var (output, _) = await ToolRunner.RunCapturedAsync("netsh", arguments, TimeoutMs, timeoutOutput: string.Empty);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }
}
