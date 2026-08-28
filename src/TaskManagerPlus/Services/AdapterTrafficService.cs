using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TaskManagerPlus.Services;

/// <summary>#584's headline gauge - current combined throughput expressed as a percent of the
/// primary adapter's own negotiated link speed. Null (the view hides the tile) when there's no
/// active adapter with a usable <see cref="NetworkInterface.Speed"/> to compare against.</summary>
public sealed record AdapterUtilizationInfo(string AdapterName, double SpeedMbps, double UtilizationPercent);

/// <summary>One adapter's share of total traffic since the last sample (#585).</summary>
public sealed record AdapterTrafficShare(string AdapterName, long BytesDelta, double SharePercent, bool IsExpectedPrimary);

/// <summary>#585's full reconciliation result for one tick. <see cref="FlagText"/> is null when
/// nothing looks unexpected (no data yet, or the expected primary adapter is carrying essentially
/// all of the traffic).</summary>
public sealed record AdapterReconciliationResult(string? PrimaryAdapterName, List<AdapterTrafficShare> Shares, string? FlagText);

/// <summary>
/// Items #584/#585 (suggestions.md "Throughput, bufferbloat and per-process bandwidth"): both
/// derived entirely from data already available every tick - <see cref="NetworkInterface.Speed"/>
/// (a negotiated link speed, no extra I/O) and each adapter's own <see cref="IPv4InterfaceStatistics"/>
/// byte counters (the same per-adapter statistics <c>HardwareMonitorService.ReadTotalNetworkBytes</c>
/// already sums across every adapter for the existing headline throughput figure) - so both ride
/// the existing 15s CheckConnectivityAsync tick rather than getting a poller of their own, per
/// CLAUDE.md's on-demand-vs-polled convention ("cheap... fine on the existing tick").
///
/// #585 needs to remember the previous tick's per-adapter byte counts to compute a delta, so
/// (unlike #584's static utilization calc) it's an instance with its own small piece of state -
/// the same "keep a previous-sample dictionary, diff against the next one" shape
/// AdapterErrorCounterService already uses for its own per-adapter deltas.
/// </summary>
public sealed class AdapterTrafficService
{
    private Dictionary<string, (long Rx, long Tx)> _previous = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _previousUtc = DateTime.MinValue;

    /// <summary>#584: expresses <paramref name="receiveBps"/>+<paramref name="sendBps"/> (already
    /// sampled every tick by the shared PerformanceViewModel) as a percent of the primary
    /// adapter's negotiated link speed.</summary>
    public static AdapterUtilizationInfo? ComputeUtilization(double receiveBps, double sendBps)
    {
        var primary = FindPrimaryAdapter();
        if (primary is null || primary.Speed <= 0) return null;

        double speedMbps = primary.Speed / 1_000_000.0;
        double usedMbps = (receiveBps + sendBps) * 8.0 / 1_000_000.0;
        double percent = speedMbps <= 0 ? 0 : Math.Clamp(usedMbps / speedMbps * 100.0, 0, 100);
        return new AdapterUtilizationInfo(primary.Name, speedMbps, percent);
    }

    /// <summary>#585: per-adapter byte-delta share since the last call, flagging any adapter
    /// other than the expected primary (the one carrying the default route, same definition
    /// NetworkDiagnosticsService.FindDefaultGateway already uses) that's carrying a meaningful
    /// share of current traffic - a virtual switch, a tethered phone, or a VPN tunnel siphoning
    /// traffic away from the adapter the user believes is active. "Quick flag, not a verdict" -
    /// a second real adapter (e.g. a Hyper-V vEthernet switch used for a local VM) can trip this
    /// just as legitimately as an unexpected one.</summary>
    public AdapterReconciliationResult ComputeReconciliation()
    {
        var now = DateTime.UtcNow;
        var current = new Dictionary<string, (long Rx, long Tx)>(StringComparer.OrdinalIgnoreCase);
        string? primaryName = FindPrimaryAdapter()?.Name;

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var stats = ni.GetIPStatistics();
                current[ni.Name] = (stats.BytesReceived, stats.BytesSent);
            }
        }
        catch
        {
            // Best-effort - fall through with whatever was gathered before the failure.
        }

        double elapsedSeconds = _previousUtc == DateTime.MinValue ? 0 : (now - _previousUtc).TotalSeconds;
        var deltas = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long totalDelta = 0;

        if (elapsedSeconds > 0)
        {
            foreach (var (name, (rx, tx)) in current)
            {
                if (!_previous.TryGetValue(name, out var prev)) continue;
                long delta = Math.Max(0, (rx - prev.Rx) + (tx - prev.Tx));
                deltas[name] = delta;
                totalDelta += delta;
            }
        }

        _previous = current;
        _previousUtc = now;

        var shares = new List<AdapterTrafficShare>();
        string? flag = null;
        foreach (var (name, delta) in deltas.OrderByDescending(d => d.Value))
        {
            double sharePercent = totalDelta <= 0 ? 0 : delta * 100.0 / totalDelta;
            bool isPrimary = primaryName is not null && name.Equals(primaryName, StringComparison.OrdinalIgnoreCase);
            shares.Add(new AdapterTrafficShare(name, delta, sharePercent, isPrimary));

            // Flag threshold: a meaningful absolute amount (>1 MB this interval) AND a meaningful
            // share (>10%) on a non-primary adapter - avoids flagging idle background chatter
            // (Windows Update, a background sync) on a secondary adapter that's really doing
            // nothing.
            if (!isPrimary && primaryName is not null && delta > 1_000_000 && sharePercent > 10)
                flag ??= $"\"{name}\" is carrying {sharePercent:0.#}% of current traffic, not \"{primaryName}\" - check for a VPN tunnel, virtual switch, or tethered device.";
        }

        return new AdapterReconciliationResult(primaryName, shares, flag);
    }

    /// <summary>The "adapter the user believes is active" - defined the same way
    /// NetworkDiagnosticsService.FindDefaultGateway already picks a primary adapter for this tab's
    /// existing connectivity check: the first active, non-loopback/non-tunnel adapter with a
    /// configured IPv4 default gateway.</summary>
    private static NetworkInterface? FindPrimaryAdapter()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var gateway = ni.GetIPProperties().GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                if (gateway is not null) return ni;
            }
        }
        catch
        {
            // fall through to "no primary adapter found"
        }
        return null;
    }
}
