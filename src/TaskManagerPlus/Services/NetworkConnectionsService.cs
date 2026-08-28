using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>Round 19, #883: how a listening socket is bound, for the exposed-listener map -
/// loopback-only is the common/expected/uninteresting case, all-interfaces is the one worth a
/// second look, and a specific bound address sits in between (reachable only via that one
/// address/adapter).</summary>
public enum ListenerBindScope { LoopbackOnly, AllInterfaces, SpecificAddress }

/// <summary>Round 19, #883: one LISTENING socket enriched with bind scope, a heuristic firewall
/// cross-reference, and the owning process's signature/publisher - see
/// NetworkConnectionsService.BuildExposedListenerMap's remarks for why every judgment here is
/// explicitly a heuristic, not a definitive reachability test.</summary>
public sealed class ExposedListenerInfo
{
    public required TcpConnectionInfo Connection { get; init; }
    public ListenerBindScope BindScope { get; init; }
    public bool FirewallLooksAllowed { get; init; }
    public string ReachabilityNote { get; init; } = string.Empty;
    public string SignatureStatus { get; init; } = "Unknown";
    public string Publisher { get; init; } = "Unknown";

    /// <summary>Whether this listener belongs to this app's own process - see #883's own explicit
    /// text: "the user sees their own exposure honestly," so this is used to call the row out
    /// clearly, never to filter it out of the list.</summary>
    public bool IsSelf { get; init; }

    public bool IsInteresting => BindScope == ListenerBindScope.AllInterfaces;
}

/// <summary>One active TCP connection, with its owning process (#21 - a themed "netstat -b"). A
/// mutable class rather than a record - same "annotate after the fact" shape RouteEntry already
/// uses - since #525's reverse-DNS enrichment fills in <see cref="RemoteHostName"/> after a row has
/// already been created, sometimes from a memory cache with no fresh I/O at all, and #559's
/// SYN_SENT-age tracking below does the same across polls rather than at construction time.</summary>
public sealed class TcpConnectionInfo
{
    public string LocalAddress { get; init; } = string.Empty;
    public int LocalPort { get; init; }
    public string RemoteAddress { get; init; } = string.Empty;
    public int RemotePort { get; init; }
    public string State { get; init; } = string.Empty;
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>#562: "IPv4" or "IPv6" - which of the two GetExtendedTcpTable calls
    /// NetworkConnectionsService.Sample now makes produced this row.</summary>
    public string AddressFamily { get; init; } = "IPv4";

    /// <summary>#525: the remote address's PTR name, filled in by ReverseDnsService - null until a
    /// resolution has actually been attempted (cached or fresh), never a guess.</summary>
    public string? RemoteHostName { get; set; }

    // #559: set by SynSentStallTracker.Annotate below - null/false/0 for every connection not
    // currently in SYN_SENT, or on the very first poll a SYN_SENT connection is ever seen.
    public DateTime? SynSentSinceUtc { get; set; }
    public double? SynSentAgeSeconds { get; set; }
    public bool IsStalledSynSent { get; set; }
    public string? StalledSynReason { get; set; }
}

/// <summary>One active UDP "connection" (really just a bound local endpoint - UDP is connectionless,
/// so there's no remote address/port or state to show, unlike <see cref="TcpConnectionInfo"/>) with
/// its owning process (#561). Read via GetExtendedUdpTable, the UDP sibling of the GetExtendedTcpTable
/// call the existing #21 grid already uses.</summary>
public sealed class UdpConnectionInfo
{
    public string LocalAddress { get; init; } = string.Empty;
    public int LocalPort { get; init; }
    public int Pid { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public string AddressFamily { get; init; } = "IPv4";
}

/// <summary>
/// Per-process connection counts (#87 - a "per-process bandwidth" proxy). Windows exposes no
/// public, stable API for true per-process byte-level network attribution - Task Manager's own
/// per-process network column is built on an undocumented NSI (Network Store Interface) call, the
/// same tier of undocumented interop this app has deliberately avoided elsewhere (see
/// ProcessMonitorService's remarks on why per-process power draw isn't shown at all for a similar
/// reason). A connection count from the same TCP table NetworkConnectionsService already reads is
/// a reasonable, honest proxy instead: the process opening/holding the most simultaneous
/// connections is very often the one saturating the link, even without a byte count attached.
/// </summary>
public sealed record NetworkProcessUsage(int Pid, string ProcessName, int ConnectionCount, int EstablishedCount);

/// <summary>#558: one connection state's count in the current sample, plus an "excess" flag/reason
/// when that count crosses one of the informal thresholds <see cref="NetworkConnectionsService.BuildStateHistogram"/>
/// applies - TIME_WAIT (port churn), CLOSE_WAIT (a leaking socket, named by owning process), and
/// SYN_SENT (outbound being blocked) each mean something different, so each gets its own worded
/// flag rather than one generic "too many" message.</summary>
public sealed record ConnectionStateHistogramEntry(string State, int Count, bool IsFlagged, string? FlagReason);

/// <summary>#560's one match for a port lookup - which table it came from (TCP/UDP), the owning
/// process, and (when readable) its image path and start time, the two things eyeballing the plain
/// connections grid can't answer.</summary>
public sealed record PortLookupResult(
    int Port, string Protocol, string AddressFamily, string LocalAddress, string State,
    int Pid, string ProcessName, string? ProcessPath, DateTime? ProcessStartTime);

/// <summary>
/// Lists active TCP connections with their owning process. .NET exposes no managed API for the
/// owning PID of a connection (IPGlobalProperties.GetActiveTcpConnections() doesn't carry one) -
/// this uses the same GetExtendedTcpTable native call `netstat -b` itself is built on. Same
/// interop-risk tier as CpuTopologyService's native calls - wrapped to return an empty list on
/// any failure rather than throwing. #561/#562 extend this with the UDP table
/// (GetExtendedUdpTable) and the IPv6 address family (both calls also accept AF_INET6), reusing
/// one generic native-table reader for all four family/protocol combinations rather than four
/// near-duplicate P/Invoke call sites.
/// </summary>
public static class NetworkConnectionsService
{
    private const int AfInet = 2; // AF_INET
    private const int AfInet6 = 23; // AF_INET6 (#562)
    private const int TcpTableOwnerPidAll = 5; // TCP_TABLE_OWNER_PID_ALL
    private const int UdpTableOwnerPid = 1; // UDP_TABLE_OWNER_PID (#561)

    public static List<TcpConnectionInfo> Sample()
    {
        var results = new List<TcpConnectionInfo>();
        var processNames = BuildProcessNameCache();

        try
        {
            foreach (var row in ReadTable<MIB_TCPROW_OWNER_PID>(AfInet, TcpTableOwnerPidAll, isUdp: false))
            {
                results.Add(new TcpConnectionInfo
                {
                    AddressFamily = "IPv4",
                    LocalAddress = new IPAddress(row.LocalAddr).ToString(),
                    LocalPort = ExtractPort(row.LocalPort),
                    RemoteAddress = new IPAddress(row.RemoteAddr).ToString(),
                    RemotePort = ExtractPort(row.RemotePort),
                    State = StateName(row.State),
                    Pid = (int)row.OwningPid,
                    ProcessName = ResolveProcessName(processNames, (int)row.OwningPid),
                });
            }
        }
        catch
        {
            // Best-effort - an empty IPv4 table just means nothing to show from this family.
        }

        try
        {
            foreach (var row in ReadTable<MIB_TCP6ROW_OWNER_PID>(AfInet6, TcpTableOwnerPidAll, isUdp: false))
            {
                results.Add(new TcpConnectionInfo
                {
                    AddressFamily = "IPv6",
                    LocalAddress = new IPAddress(row.LocalAddr, row.LocalScopeId).ToString(),
                    LocalPort = ExtractPort(row.LocalPort),
                    RemoteAddress = new IPAddress(row.RemoteAddr, row.RemoteScopeId).ToString(),
                    RemotePort = ExtractPort(row.RemotePort),
                    State = StateName(row.State),
                    Pid = (int)row.OwningPid,
                    ProcessName = ResolveProcessName(processNames, (int)row.OwningPid),
                });
            }
        }
        catch
        {
            // #562: IPv6 can be disabled/unsupported on some machines - degrade to IPv4-only
            // rather than losing the whole table over one family's failure.
        }

        return results;
    }

    /// <summary>#561: the UDP sibling of <see cref="Sample"/> - both address families, same
    /// per-family-isolated degrade-on-failure shape.</summary>
    public static List<UdpConnectionInfo> SampleUdp()
    {
        var results = new List<UdpConnectionInfo>();
        var processNames = BuildProcessNameCache();

        try
        {
            foreach (var row in ReadTable<MIB_UDPROW_OWNER_PID>(AfInet, UdpTableOwnerPid, isUdp: true))
            {
                results.Add(new UdpConnectionInfo
                {
                    AddressFamily = "IPv4",
                    LocalAddress = new IPAddress(row.LocalAddr).ToString(),
                    LocalPort = ExtractPort(row.LocalPort),
                    Pid = (int)row.OwningPid,
                    ProcessName = ResolveProcessName(processNames, (int)row.OwningPid),
                });
            }
        }
        catch
        {
            // Best-effort - an empty IPv4 UDP table just means nothing to show from this family.
        }

        try
        {
            foreach (var row in ReadTable<MIB_UDP6ROW_OWNER_PID>(AfInet6, UdpTableOwnerPid, isUdp: true))
            {
                results.Add(new UdpConnectionInfo
                {
                    AddressFamily = "IPv6",
                    LocalAddress = new IPAddress(row.LocalAddr, row.LocalScopeId).ToString(),
                    LocalPort = ExtractPort(row.LocalPort),
                    Pid = (int)row.OwningPid,
                    ProcessName = ResolveProcessName(processNames, (int)row.OwningPid),
                });
            }
        }
        catch
        {
            // #562-adjacent: same "degrade to IPv4-only" tolerance for the UDP table's IPv6 half.
        }

        return results;
    }

    // Round 19, #883: loopback vs. all-interfaces vs. a specific bound address.
    private static ListenerBindScope ClassifyBindScope(string localAddress) => localAddress switch
    {
        "127.0.0.1" or "::1" => ListenerBindScope.LoopbackOnly,
        "0.0.0.0" or "::" => ListenerBindScope.AllInterfaces,
        _ => ListenerBindScope.SpecificAddress,
    };

    /// <summary>
    /// Round 19, #883: extends the plain LISTENING rows from <see cref="Sample"/> with bind scope,
    /// a heuristic firewall cross-reference against #882's enabled-inbound-allow-rule audit, and
    /// the owning process's signature/publisher. This is a heuristic cross-reference, not a
    /// definitive reachability test - a matching rule (by port only, ignoring profile/program/
    /// remote-scope narrowing within that rule) counts as "looks allowed," and the absence of one
    /// only counts as "not obviously reachable" when every currently-active firewall profile's own
    /// default inbound action is Block; anything less certain is reported as "reachability unclear"
    /// rather than guessing either way. Explicitly does NOT filter out this app's own optional
    /// remote-monitor listener (RemoteMonitorService) when it's running - see IsSelf's remarks.
    /// </summary>
    public static List<ExposedListenerInfo> BuildExposedListenerMap(
        IEnumerable<TcpConnectionInfo> connections,
        List<FirewallService.FirewallRuleInfo> enabledInboundAllowRules,
        bool everyActiveProfileDefaultsToBlockInbound)
    {
        var results = new List<ExposedListenerInfo>();
        int selfPid = Environment.ProcessId;

        foreach (var c in connections.Where(c => c.State == "LISTENING"))
        {
            var scope = ClassifyBindScope(c.LocalAddress);
            bool firewallAllowed = enabledInboundAllowRules.Any(r => FirewallService.RuleCoversLocalPort(r, c.LocalPort));

            string note = firewallAllowed
                ? "A currently-enabled inbound Allow rule matches this port - looks reachable from wherever that rule's scope permits."
                : everyActiveProfileDefaultsToBlockInbound
                    ? "No matching enabled inbound Allow rule found, and every active firewall profile defaults to Block - not obviously reachable, but this is a heuristic cross-reference, not a definitive test."
                    : "No matching enabled inbound Allow rule found, but this app couldn't confirm every active profile defaults to Block - reachability unclear.";

            string path = ResolveProcessPath(c.Pid);
            string sig = path.Length > 0 ? SignatureCheckService.GetStatus(path) : "Unknown";
            var (subjectCn, issuerCn, _, _, _) = path.Length > 0 ? SignatureCheckService.GetSignerInfo(path) : (null, null, null, null, false);

            results.Add(new ExposedListenerInfo
            {
                Connection = c,
                BindScope = scope,
                FirewallLooksAllowed = firewallAllowed,
                ReachabilityNote = note,
                SignatureStatus = sig,
                Publisher = subjectCn ?? issuerCn ?? "Unknown",
                IsSelf = c.Pid == selfPid,
            });
        }

        return results;
    }

    private static string ResolveProcessPath(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            // Denied (protected process) or already exited - "Unknown" signature/publisher below.
            return string.Empty;
        }
    }

    /// <summary>#87: groups an already-sampled connection list by owning process, sorted by
    /// connection count descending - see NetworkProcessUsage's remarks for why this is a
    /// connection-count proxy rather than a true byte-level figure.</summary>
    public static List<NetworkProcessUsage> SummarizeByProcess(IEnumerable<TcpConnectionInfo> connections) =>
        connections
            .GroupBy(c => (c.Pid, c.ProcessName))
            .Select(g => new NetworkProcessUsage(g.Key.Pid, g.Key.ProcessName, g.Count(), g.Count(c => c.State == "ESTABLISHED")))
            .OrderByDescending(u => u.ConnectionCount)
            .ToList();

    // #558: informal "worth a look" thresholds - deliberately low enough to catch a real problem on
    // an ordinary desktop's connection count, not a tuned production-server baseline. Worded as a
    // flag with a stated reason, never a hard verdict, matching this app's other pattern-matched
    // indicators.
    private const int TimeWaitFlagThreshold = 50;
    private const int CloseWaitFlagThreshold = 5;
    private const int SynSentFlagThreshold = 3;

    /// <summary>#558: groups the already-sampled connection list by state, flagging an excess of
    /// TIME_WAIT (port churn - usually harmless on its own), CLOSE_WAIT (a lingering local socket
    /// the remote side already closed - the closest thing to a leak signature this table can show,
    /// named by owning process), or SYN_SENT (outbound connection attempts that never completed -
    /// worth checking against a firewall/proxy). No extra I/O - purely derived from the connections
    /// Sample() already returned.</summary>
    public static List<ConnectionStateHistogramEntry> BuildStateHistogram(IEnumerable<TcpConnectionInfo> connections)
    {
        var list = connections.ToList();
        var entries = new List<ConnectionStateHistogramEntry>();

        foreach (var group in list.GroupBy(c => c.State).OrderByDescending(g => g.Count()))
        {
            int count = group.Count();
            string? reason = group.Key switch
            {
                "TIME_WAIT" when count >= TimeWaitFlagThreshold =>
                    $"{count} connections sitting in TIME_WAIT - usually just rapid connection churn (lots of short-lived connections opening and closing), not a fault by itself.",
                "CLOSE_WAIT" when count >= CloseWaitFlagThreshold =>
                    $"{count} connections stuck in CLOSE_WAIT (remote side already closed, local app hasn't) held by {DescribeTopProcesses(list, "CLOSE_WAIT")} - can point at a socket leak in that process.",
                "SYN_SENT" when count >= SynSentFlagThreshold =>
                    $"{count} outbound connections stuck in SYN_SENT - can mean a firewall, proxy, or dead route is silently dropping them. See the age column below for how long each has been stuck.",
                _ => null,
            };
            entries.Add(new ConnectionStateHistogramEntry(group.Key, count, reason is not null, reason));
        }

        return entries;
    }

    private static string DescribeTopProcesses(List<TcpConnectionInfo> connections, string state) =>
        string.Join(", ", connections
            .Where(c => c.State == state)
            .GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => $"{g.Key} ({g.Count()})"));

    /// <summary>Item #560: "what is using port N" - scans the already-sampled TCP and UDP tables
    /// (every address family and state, including bare listeners) for a given local port and
    /// enriches each match with its owning process's image path and start time - the two things
    /// eyeballing the plain connections grid can't answer. Reading MainModule/StartTime can be
    /// denied even from this (elevated) app on a handful of protected system processes - degrades
    /// those two fields to null rather than failing the whole lookup.</summary>
    public static List<PortLookupResult> FindByPort(int port, IEnumerable<TcpConnectionInfo> tcp, IEnumerable<UdpConnectionInfo> udp)
    {
        var results = new List<PortLookupResult>();

        foreach (var c in tcp.Where(c => c.LocalPort == port))
        {
            var (path, started) = ReadProcessDetails(c.Pid);
            results.Add(new PortLookupResult(port, "TCP", c.AddressFamily, c.LocalAddress, c.State, c.Pid, c.ProcessName, path, started));
        }

        foreach (var u in udp.Where(u => u.LocalPort == port))
        {
            var (path, started) = ReadProcessDetails(u.Pid);
            results.Add(new PortLookupResult(port, "UDP", u.AddressFamily, u.LocalAddress, "(stateless)", u.Pid, u.ProcessName, path, started));
        }

        return results;
    }

    private static (string? Path, DateTime? StartTime) ReadProcessDetails(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            string? path = null;
            try { path = proc.MainModule?.FileName; } catch { /* access denied on a protected process - leave null, never guess */ }
            DateTime? start = null;
            try { start = proc.StartTime; } catch { /* same as above */ }
            return (path, start);
        }
        catch
        {
            return (null, null); // process exited between the table sample and this lookup
        }
    }

    private static Dictionary<int, string> BuildProcessNameCache()
    {
        var dict = new Dictionary<int, string>();
        foreach (var p in Process.GetProcesses())
        {
            try { dict[p.Id] = p.ProcessName; } catch { /* ignore */ }
            finally { p.Dispose(); }
        }
        return dict;
    }

    private static string ResolveProcessName(Dictionary<int, string> processNames, int pid) =>
        processNames.TryGetValue(pid, out var name) ? name : "(unknown)";

    /// <summary>The native struct's port fields are a DWORD but only the low 16 bits are used, in
    /// network (big-endian) byte order - swap to host order to get the real port number. Shared by
    /// every TCP/UDP, v4/v6 row type below - the port encoding is identical across all four.</summary>
    private static int ExtractPort(uint raw) => IPAddress.NetworkToHostOrder((short)(ushort)raw) & 0xFFFF;

    private static string StateName(uint state) => state switch
    {
        1 => "CLOSED", 2 => "LISTENING", 3 => "SYN_SENT", 4 => "SYN_RCVD",
        5 => "ESTABLISHED", 6 => "FIN_WAIT1", 7 => "FIN_WAIT2", 8 => "CLOSE_WAIT",
        9 => "CLOSING", 10 => "LAST_ACK", 11 => "TIME_WAIT", 12 => "DELETE_TCB",
        _ => "UNKNOWN",
    };

    /// <summary>One generic native-table reader shared by TCP v4/v6 (GetExtendedTcpTable) and UDP
    /// v4/v6 (GetExtendedUdpTable, #561) - all four calls share the same "ask for the size, allocate,
    /// call again, read a 4-byte row count then a packed row array" shape, differing only in which
    /// native function to call, the address family, the table class, and the row struct layout.</summary>
    private static List<T> ReadTable<T>(int family, int tableClass, bool isUdp) where T : struct
    {
        var list = new List<T>();
        int bufSize = 0;
        NativeGetTable(isUdp, IntPtr.Zero, ref bufSize, family, tableClass);
        if (bufSize <= 0) return list;

        IntPtr buffer = Marshal.AllocHGlobal(bufSize);
        try
        {
            uint result = NativeGetTable(isUdp, buffer, ref bufSize, family, tableClass);
            if (result != 0) return list; // non-zero = a Win32 error, not NO_ERROR

            int numEntries = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf<T>();
            for (int i = 0; i < numEntries; i++)
                list.Add(Marshal.PtrToStructure<T>(IntPtr.Add(rowPtr, i * rowSize)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    private static uint NativeGetTable(bool isUdp, IntPtr buffer, ref int bufSize, int family, int tableClass) => isUdp
        ? GetExtendedUdpTable(buffer, ref bufSize, sort: true, family, tableClass, 0)
        : GetExtendedTcpTable(buffer, ref bufSize, sort: true, family, tableClass, 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    /// <summary>#562: MIB_TCP6ROW_OWNER_PID - the IPv6 sibling of <see cref="MIB_TCPROW_OWNER_PID"/>,
    /// 16-byte addresses plus a scope ID instead of a bare DWORD.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    /// <summary>#561: MIB_UDPROW_OWNER_PID - no state, no remote endpoint (UDP is connectionless).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    /// <summary>#561/#562 combined: MIB_UDP6ROW_OWNER_PID.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }
}

/// <summary>
/// Item #559: tracks how long each connection has sat continuously in SYN_SENT across successive
/// <see cref="NetworkConnectionsService.Sample"/> calls, and flags any that persist past
/// <see cref="StallThresholdSeconds"/> - "the precise signature of a firewall, proxy or dead route
/// silently swallowing outbound traffic" this item calls out. An instance (not static) class
/// because it has to remember each connection's first-seen timestamp between calls, the same
/// "instantiate once, call every tick" shape AdapterErrorCounterService already uses for its own
/// per-cycle state. Since the connections grid refreshes on this tab's existing 15s tick rather than
/// a fast per-second poll, the recorded age can lag true onset by up to one interval - the UI caption
/// says so rather than claiming second-level precision.
/// </summary>
public sealed class SynSentStallTracker
{
    private const double StallThresholdSeconds = 5.0;

    private readonly Dictionary<string, DateTime> _firstSeenUtc = new(StringComparer.Ordinal);

    /// <summary>Mutates every SYN_SENT row in <paramref name="connections"/> in place (see
    /// TcpConnectionInfo's remarks for why this class's fields are mutable) and forgets any
    /// previously-tracked connection that isn't SYN_SENT (or isn't present at all) this time - a
    /// connection that completed, was reset, or simply aged out of the table starts fresh if it
    /// ever shows SYN_SENT again later.</summary>
    public void Annotate(IEnumerable<TcpConnectionInfo> connections)
    {
        var now = DateTime.UtcNow;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in connections)
        {
            if (c.State != "SYN_SENT") continue;

            string key = $"{c.Pid}|{c.AddressFamily}|{c.LocalAddress}:{c.LocalPort}|{c.RemoteAddress}:{c.RemotePort}";
            seenKeys.Add(key);

            if (!_firstSeenUtc.TryGetValue(key, out var firstSeen))
            {
                firstSeen = now;
                _firstSeenUtc[key] = firstSeen;
            }

            double age = (now - firstSeen).TotalSeconds;
            c.SynSentSinceUtc = firstSeen;
            c.SynSentAgeSeconds = age;
            c.IsStalledSynSent = age >= StallThresholdSeconds;
            c.StalledSynReason = c.IsStalledSynSent
                ? $"Stuck in SYN_SENT for {age:0}s - {c.ProcessName} (PID {c.Pid}) trying to reach {c.RemoteAddress}:{c.RemotePort}. Often a firewall, proxy, or dead route silently dropping the handshake."
                : null;
        }

        foreach (var stale in _firstSeenUtc.Keys.Where(k => !seenKeys.Contains(k)).ToList())
            _firstSeenUtc.Remove(stale);
    }
}

/// <summary>
/// Item #525: on-demand reverse-DNS (PTR) enrichment for the existing #21 connections grid, so a
/// wall of remote IPs becomes recognizable CDN/service names. A plain <see cref="Dns.GetHostEntryAsync"/>
/// call per unique remote address (the managed API is the right tool here, not a tool shell-out or
/// raw UDP query - a single PTR lookup per IP has none of the "runs forever" cost profile that
/// makes DnsResponseTimeMonitorService's #526 raw-socket approach worth the extra code), each
/// bounded by its own strict timeout so one unresponsive/nonexistent PTR record can't stall the
/// whole batch. Resolved (and failed) names are cached in memory per remote IP for the lifetime of
/// the app, so re-resolving after the next 15s connections refresh - or clicking "Resolve names"
/// again - never re-queries an address already looked up.
/// </summary>
public static class ReverseDnsService
{
    private const int TimeoutMs = 800;

    // Null is a cached "looked up, nothing came back" result - distinct from "never looked up at
    // all" (not present in the dictionary), so a failed PTR lookup isn't retried every refresh.
    private static readonly ConcurrentDictionary<string, string?> Cache = new();

    /// <summary>Applies whatever's already cached to <paramref name="connections"/> - no I/O, safe
    /// to call on every periodic refresh so a name resolved once via <see cref="ResolveNamesAsync"/>
    /// stays visible across subsequent polls without needing to be re-resolved.</summary>
    public static void ApplyCached(IEnumerable<TcpConnectionInfo> connections)
    {
        foreach (var c in connections)
            if (Cache.TryGetValue(c.RemoteAddress, out var name)) c.RemoteHostName = name;
    }

    /// <summary>#525: resolves every not-yet-cached remote address among <paramref name="connections"/>
    /// in parallel, then applies the (now fully populated) cache to all of them.</summary>
    public static async Task ResolveNamesAsync(IEnumerable<TcpConnectionInfo> connections)
    {
        var snapshot = connections.ToList();
        var toResolve = snapshot
            .Select(c => c.RemoteAddress)
            .Where(ip => ip != "0.0.0.0" && ip != "::" && !Cache.ContainsKey(ip))
            .Distinct()
            .ToList();

        var tasks = toResolve.Select(async ip => Cache[ip] = await ResolveOneAsync(ip));
        await Task.WhenAll(tasks);

        ApplyCached(snapshot);
    }

    private static async Task<string?> ResolveOneAsync(string ip)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeoutMs);
            var entry = await Dns.GetHostEntryAsync(ip, cts.Token);
            return string.IsNullOrWhiteSpace(entry.HostName) || entry.HostName.Equals(ip, StringComparison.OrdinalIgnoreCase)
                ? null
                : entry.HostName;
        }
        catch
        {
            // No PTR record, timed out, or the resolver refused - degrade to null (shown as the
            // bare IP), never fabricate a name.
            return null;
        }
    }
}

/// <summary>Current Wi-Fi association details (#23), from parsing `netsh wlan show interfaces` -
/// there's no managed API for RSSI/channel short of the native WLAN API, and parsing netsh's own
/// text output is the same lower-effort technique many diagnostic tools use for this. A real
/// limitation: the field labels below ("SSID", "Signal", "Channel", "Radio type") are
/// English-locale text netsh prints, so this silently returns null on a non-English Windows
/// install rather than misparsing - the Network tab just hides the Wi-Fi card in that case, the
/// same "hidden when not applicable" pattern the Battery section already uses.</summary>
public sealed record WifiInfo(string Ssid, int? SignalPercent, int? Channel, string RadioType);

public static class WifiDiagnosticsService
{
    public static async Task<WifiInfo?> ReadCurrentWifiAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            // Concurrent async reads + a bounded WaitForExitAsync + Kill()-on-timeout - the same
            // pattern TracerouteService.RunAsync uses, rather than the previous synchronous
            // ReadToEnd() followed by an unchecked WaitForExit(2000), which risked a deadlock and
            // an orphaned netsh.exe process if it ever ran long.
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(2000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return null;
            }

            string output = (await outputTask) + (await errorTask);

            string? ssid = ExtractField(output, "SSID");
            if (string.IsNullOrWhiteSpace(ssid)) return null;

            string? signalRaw = ExtractField(output, "Signal");
            string? channelRaw = ExtractField(output, "Channel");
            string? radioType = ExtractField(output, "Radio type");

            int? signalPercent = signalRaw is not null && int.TryParse(signalRaw.TrimEnd('%'), out var sp) ? sp : null;
            int? channel = channelRaw is not null && int.TryParse(channelRaw, out var ch) ? ch : null;

            return new WifiInfo(ssid, signalPercent, channel, radioType ?? string.Empty);
        }
        catch
        {
            return null;
        }
    }

    private static readonly Regex LabelLineRegex = new(@"^\s*([^:]+?)\s*:\s*(.*)$", RegexOptions.Compiled);

    private static string? ExtractField(string output, string label)
    {
        foreach (var line in output.Split('\n'))
        {
            var match = LabelLineRegex.Match(line.TrimEnd('\r'));
            if (match.Success && match.Groups[1].Value.Equals(label, StringComparison.OrdinalIgnoreCase))
                return match.Groups[2].Value.Trim();
        }
        return null;
    }
}
