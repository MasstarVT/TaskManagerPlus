using System.Management;

namespace TaskManagerPlus.Services;

/// <summary>One family's (IPv4 or IPv6) TCP health reading (#557). The *PerSec fields are this
/// class's own computed rate (delta over the elapsed wall-clock time since the previous sample -
/// see TcpHealthCounterService's remarks for why a raw WMI counter needs that math done manually).
/// <see cref="RetransmitRatePercent"/> is retransmitted segments as a percentage of segments sent
/// in the same window - "above a couple of percent is the cleanest objective evidence of a lossy
/// path" per this item's own text. ConnectionFailures/ConnectionsReset are shown both as a delta
/// since the previous sample and as a running total for this app session, since a handful of resets
/// right after the app started reads very differently from a steady trickle. <see cref="IsAvailable"/>
/// false means the WMI class couldn't be read at all (e.g. IPv6 perf counters disabled) - every
/// numeric field is 0 in that case, never a guess.</summary>
public sealed record TcpHealthSample(
    string AddressFamily,
    double SegmentsRetransmittedPerSec,
    double SegmentsSentPerSec,
    double RetransmitRatePercent,
    long ConnectionFailuresDelta,
    long ConnectionFailuresTotal,
    long ConnectionsResetDelta,
    long ConnectionsResetTotal,
    bool IsAvailable);

/// <summary>
/// Item #557: TCP-layer retransmit and reset counters from Win32_PerfRawData_Tcpip_TCPv4 and
/// ..._TCPv6 - the suggestion text's own named data source. Distinct from the existing #32 reading
/// (HardwareMonitorService's "TCPv4" System.Diagnostics.PerformanceCounter, shown on the "Adapter
/// errors" card), which only ever covers IPv4 and doesn't expose ConnectionFailures/ConnectionsReset
/// at all - CLAUDE.md's own guidance for this chunk calls out WMI specifically for this item since
/// there's no simple netsh/sc-style tool text for it.
///
/// A WMI PerfRawData class's *Persec fields are running totals, not an already-computed rate
/// (unlike PerformanceCounter, which does that division internally on every NextValue() call) - this
/// class keeps the previous raw reading (and when it was taken) per family so it can compute its own
/// (value2-value1)/elapsedSeconds rate, the same "remember the previous sample, diff against it"
/// shape AdapterErrorCounterService already uses for its own per-tick deltas. An instance (not
/// static) class for that reason. The very first call for a family has nothing to diff against, so
/// it reports a zero rate rather than a nonsensical one-tick average of the whole time since boot.
/// </summary>
public sealed class TcpHealthCounterService
{
    private readonly Dictionary<string, (DateTime Utc, uint Retransmitted, uint Sent, uint ConnFailures, uint ConnReset)> _previous =
        new(StringComparer.Ordinal);

    public List<TcpHealthSample> Sample() => new()
    {
        SampleFamily("IPv4", "Win32_PerfRawData_Tcpip_TCPv4"),
        SampleFamily("IPv6", "Win32_PerfRawData_Tcpip_TCPv6"),
    };

    private TcpHealthSample SampleFamily(string family, string wmiClass)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT SegmentsRetransmittedPersec, SegmentsSentPersec, ConnectionFailures, ConnectionsReset FROM {wmiClass}");
            foreach (ManagementObject mo in searcher.Get())
            {
                uint retransmitted = ReadUInt(mo, "SegmentsRetransmittedPersec");
                uint sent = ReadUInt(mo, "SegmentsSentPersec");
                uint connFailures = ReadUInt(mo, "ConnectionFailures");
                uint connReset = ReadUInt(mo, "ConnectionsReset");
                var now = DateTime.UtcNow;

                TcpHealthSample sample;
                if (_previous.TryGetValue(family, out var prev))
                {
                    // Clamp a negative diff to 0 rather than reporting a nonsensical negative rate -
                    // these are monotonically-increasing counters, but the underlying stack can reset
                    // them (e.g. a network stack reset/repair), same tolerance
                    // AdapterErrorCounterService already applies to its own cumulative counters.
                    double elapsedSeconds = Math.Max(0.001, (now - prev.Utc).TotalSeconds);
                    double retransRate = Math.Max(0, retransmitted - prev.Retransmitted) / elapsedSeconds;
                    double sentRate = Math.Max(0, sent - prev.Sent) / elapsedSeconds;
                    long failuresDelta = Math.Max(0, (long)connFailures - prev.ConnFailures);
                    long resetDelta = Math.Max(0, (long)connReset - prev.ConnReset);

                    sample = new TcpHealthSample(
                        family,
                        Math.Round(retransRate, 2),
                        Math.Round(sentRate, 2),
                        sentRate > 0 ? Math.Round(retransRate / sentRate * 100.0, 2) : 0,
                        failuresDelta, connFailures, resetDelta, connReset, true);
                }
                else
                {
                    // Nothing to diff against yet - report a zero rate/delta, not the lifetime total
                    // masquerading as a one-tick figure.
                    sample = new TcpHealthSample(family, 0, 0, 0, 0, connFailures, 0, connReset, true);
                }

                _previous[family] = (now, retransmitted, sent, connFailures, connReset);
                return sample;
            }
        }
        catch
        {
            // WMI namespace/class missing (unusual network stack, IPv6 perf counters disabled, ...)
            // - degrade to "not available" rather than throwing past this class.
        }
        return new TcpHealthSample(family, 0, 0, 0, 0, 0, 0, 0, false);
    }

    private static uint ReadUInt(ManagementObject mo, string property)
    {
        try { return Convert.ToUInt32(mo[property]); }
        catch { return 0; }
    }
}
