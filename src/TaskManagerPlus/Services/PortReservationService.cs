using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>One TCP port-exclusion range from `netsh int ipv4 show excludedportrange
/// protocol=tcp` (#563) - Windows reserves these for its own use, and a bind to any port inside
/// one fails even though the port shows as free in a plain netstat listing. Hyper-V/WinNAT virtual
/// switches are the best-known cause of a large, unexpected block here (the "administered" ones
/// netsh marks with a trailing *).</summary>
public sealed record ExcludedPortRange(int StartPort, int EndPort, bool IsAdministered)
{
    public int PortCount => EndPort - StartPort + 1;
}

/// <summary>The TCP ephemeral (dynamic/outbound) port range from `netsh int ipv4 show
/// dynamicport tcp` (#563) - the pool Windows assigns a local port from for any outbound
/// connection that doesn't bind an explicit one.</summary>
public sealed record DynamicPortRange(int StartPort, int PortCount)
{
    public int EndPort => StartPort + PortCount - 1;
}

/// <summary>#563's combined read: every excluded range, the dynamic port range, and which excluded
/// ranges actually overlap it - the specific "port is free in netstat but the app still can't bind"
/// bug this item calls out. <see cref="CommandsSucceeded"/> false means netsh itself couldn't be
/// read (degrade to empty/null, never fabricate "no exclusions").</summary>
public sealed record PortReservationInfo(
    List<ExcludedPortRange> ExcludedRanges,
    DynamicPortRange? DynamicRange,
    List<ExcludedPortRange> OverlappingExclusions,
    bool CommandsSucceeded)
{
    public static readonly PortReservationInfo Empty = new(new List<ExcludedPortRange>(), null, new List<ExcludedPortRange>(), false);
}

/// <summary>One process's contribution to #564's ephemeral-port count - a plain record (not a
/// ValueTuple) specifically so its members bind by name in XAML; WPF's binding engine can't see a
/// compiler-synthesized tuple element name at runtime.</summary>
public sealed record PortContributor(string ProcessName, int Count);

/// <summary>#564: ephemeral-port utilization - how many of the currently-open local TCP/UDP ports
/// fall inside the dynamic range above, against that range's size. Windows doesn't literally refuse
/// a new outbound connection at some exact percentage (TIME_WAIT reuse and the real allocation
/// algorithm make the practical ceiling fuzzier than a hard number), so <see cref="IsHighUtilization"/>
/// is worded as "worth investigating," not a predicted failure.</summary>
public sealed record PortExhaustionInfo(
    int ConnectionsInRange, int RangeSize, double UtilizationPercent, bool IsHighUtilization,
    List<PortContributor> TopContributors);

/// <summary>
/// Items #563/#564 (suggestions.md "TCP stack, connections and ports"): reads the TCP port
/// exclusion/dynamic-port configuration via netsh (the standard tool for this, per CLAUDE.md's
/// "known tool over raw interop" convention - there's no WMI class or simple managed API for
/// either), flags any excluded range that swallows part of the dynamic range (#563), and separately
/// computes how full that dynamic range currently is from whatever TCP/UDP tables the caller already
/// has in hand (#564) - no extra process launch for the exhaustion half at all.
/// </summary>
public static class PortReservationService
{
    private const double HighUtilizationPercent = 80.0;

    public static async Task<PortReservationInfo> ReadAsync()
    {
        try
        {
            string excludedOutput = await RunNetshAsync("int ipv4 show excludedportrange protocol=tcp");
            string dynamicOutput = await RunNetshAsync("int ipv4 show dynamicport tcp");

            var excluded = ParseExcludedRanges(excludedOutput);
            var dynamicRange = ParseDynamicRange(dynamicOutput);

            var overlapping = dynamicRange is null
                ? new List<ExcludedPortRange>()
                : excluded.Where(e => e.StartPort <= dynamicRange.EndPort && e.EndPort >= dynamicRange.StartPort).ToList();

            bool succeeded = excludedOutput.Length > 0 && dynamicOutput.Length > 0;
            return new PortReservationInfo(excluded, dynamicRange, overlapping, succeeded);
        }
        catch
        {
            return PortReservationInfo.Empty;
        }
    }

    /// <summary>#564: pure, no extra I/O - counts already-sampled local TCP + UDP ports that fall
    /// inside <paramref name="dynamicRange"/>, and the top processes contributing them. Null when
    /// there's no dynamic range to compare against yet (netsh hasn't been read, or it failed) -
    /// the view hides the whole section in that case rather than showing a 0% that isn't real.</summary>
    public static PortExhaustionInfo? ComputeExhaustion(
        IEnumerable<TcpConnectionInfo> tcpConnections, IEnumerable<UdpConnectionInfo> udpConnections, DynamicPortRange? dynamicRange)
    {
        if (dynamicRange is null || dynamicRange.PortCount <= 0) return null;

        bool InRange(int port) => port >= dynamicRange.StartPort && port <= dynamicRange.EndPort;

        var contributors = tcpConnections.Where(c => InRange(c.LocalPort)).Select(c => c.ProcessName)
            .Concat(udpConnections.Where(u => InRange(u.LocalPort)).Select(u => u.ProcessName))
            .ToList();

        int count = contributors.Count;
        double pct = Math.Round(100.0 * count / dynamicRange.PortCount, 1);

        var top = contributors
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PortContributor(g.Key, g.Count()))
            .OrderByDescending(t => t.Count)
            .Take(5)
            .ToList();

        return new PortExhaustionInfo(count, dynamicRange.PortCount, pct, pct >= HighUtilizationPercent, top);
    }

    private static async Task<string> RunNetshAsync(string args)
    {
        var psi = new ProcessStartInfo("netsh.exe", args)
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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

    // Matches a data row of `netsh int ipv4 show excludedportrange` - two right-aligned numeric
    // columns (Start Port, End Port), optionally followed by netsh's own "*" administered-exclusion
    // marker. Header/separator/footnote lines simply don't match, so they're skipped rather than
    // needing to be explicitly excluded.
    private static readonly Regex ExcludedRangeLineRegex = new(@"^\s*(\d+)\s+(\d+)\s*(\*)?\s*$", RegexOptions.Compiled);

    private static List<ExcludedPortRange> ParseExcludedRanges(string output)
    {
        var list = new List<ExcludedPortRange>();
        foreach (var rawLine in output.Split('\n'))
        {
            var match = ExcludedRangeLineRegex.Match(rawLine.TrimEnd('\r'));
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[1].Value, out int start)) continue;
            if (!int.TryParse(match.Groups[2].Value, out int end)) continue;
            list.Add(new ExcludedPortRange(start, end, match.Groups[3].Success));
        }
        return list;
    }

    private static DynamicPortRange? ParseDynamicRange(string output)
    {
        int? start = ExtractIntField(output, "Start Port");
        int? count = ExtractIntField(output, "Number of Ports");
        return start is int s && count is int c ? new DynamicPortRange(s, c) : null;
    }

    private static int? ExtractIntField(string output, string label)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            int idx = line.IndexOf(':');
            if (idx < 0) continue;
            if (!line[..idx].Trim().Equals(label, StringComparison.OrdinalIgnoreCase)) continue;
            return int.TryParse(line[(idx + 1)..].Trim(), out int value) ? value : null;
        }
        return null;
    }
}
