using System.Net.NetworkInformation;

namespace TaskManagerPlus.Services;

/// <summary>One adapter's per-tick error/discard delta (#547) - the change in each cumulative
/// counter since the previous sample, not the running total the existing "Adapter errors" card
/// (HardwareMonitorService.ReadNetworkErrorCounters, a machine-wide sum) already shows.
/// <see cref="HasNonZeroRate"/> is the "flag any non-zero rate" signal #547 calls for - on Ethernet,
/// a non-zero receive-error rate specifically almost always means a bad cable or port.</summary>
public sealed record AdapterErrorCounters(
    string AdapterName, long ReceivedErrorsDelta, long OutboundErrorsDelta,
    long ReceivedDiscardsDelta, long OutboundDiscardsDelta, bool HasNonZeroRate);

/// <summary>
/// Item #547: per-adapter PacketsReceivedErrors/PacketsOutboundErrors/PacketsReceivedDiscarded/
/// PacketsOutboundDiscarded, sampled as a delta between calls rather than the cumulative total.
///
/// The suggestion text names Win32_PerfRawData_Tcpip_NetworkInterface or GetIfEntry2 as the data
/// source, but .NET's own <see cref="NetworkInterface.GetIPStatistics"/> already exposes the exact
/// same four cumulative fields per adapter without a WMI query or raw interop - it's the same API
/// HardwareMonitorService.ReadNetworkErrorCounters already uses for its own (summed, cumulative)
/// "Adapter errors" card, so this reuses it rather than adding a second, redundant data path. An
/// instance (not static) class because it has to remember each adapter's previous raw reading
/// between calls to compute a delta - the same "instantiate once, call every tick" shape
/// LatencyMonitorService's probe ring already uses for its own per-cycle state.
/// </summary>
public sealed class AdapterErrorCounterService
{
    private readonly Dictionary<string, (long RxErr, long TxErr, long RxDisc, long TxDisc)> _previous =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads every active, physical-looking adapter's current cumulative counters and
    /// diffs them against whatever was read last call. The very first call for a given adapter has
    /// nothing to diff against, so it reports a zero delta rather than the full cumulative total
    /// masquerading as a one-tick rate.</summary>
    public List<AdapterErrorCounters> Sample()
    {
        var result = new List<AdapterErrorCounters>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                IPInterfaceStatistics stats;
                try { stats = ni.GetIPStatistics(); }
                catch { continue; } // this one adapter's counters aren't readable - skip it, not the whole sample

                long rxErr = stats.IncomingPacketsWithErrors;
                long txErr = stats.OutgoingPacketsWithErrors;
                long rxDisc = stats.IncomingPacketsDiscarded;
                long txDisc = stats.OutgoingPacketsDiscarded;
                seenNames.Add(ni.Name);

                if (_previous.TryGetValue(ni.Name, out var prev))
                {
                    // These are monotonically-increasing counters, but a driver reload/reconnect
                    // can reset them to 0 - clamp a negative diff to 0 rather than reporting a
                    // nonsensical negative "error rate".
                    long dRxErr = Math.Max(0, rxErr - prev.RxErr);
                    long dTxErr = Math.Max(0, txErr - prev.TxErr);
                    long dRxDisc = Math.Max(0, rxDisc - prev.RxDisc);
                    long dTxDisc = Math.Max(0, txDisc - prev.TxDisc);
                    result.Add(new AdapterErrorCounters(ni.Name, dRxErr, dTxErr, dRxDisc, dTxDisc,
                        dRxErr > 0 || dTxErr > 0 || dRxDisc > 0 || dTxDisc > 0));
                }
                else
                {
                    result.Add(new AdapterErrorCounters(ni.Name, 0, 0, 0, 0, false));
                }
                _previous[ni.Name] = (rxErr, txErr, rxDisc, txDisc);
            }

            // Forget adapters that disappeared (renamed, unplugged, disabled) so this doesn't grow
            // unbounded across a long-running session.
            foreach (var stale in _previous.Keys.Where(k => !seenNames.Contains(k)).ToList())
                _previous.Remove(stale);
        }
        catch
        {
            // Best-effort - return whatever was gathered before the failure.
        }
        return result;
    }
}
