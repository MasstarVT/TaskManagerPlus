using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>One active TCP connection, with its owning process (#21 - a themed "netstat -b").</summary>
public sealed record TcpConnectionInfo(
    string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, string State, int Pid, string ProcessName);

/// <summary>
/// Lists active TCP connections with their owning process. .NET exposes no managed API for the
/// owning PID of a connection (IPGlobalProperties.GetActiveTcpConnections() doesn't carry one) -
/// this uses the same GetExtendedTcpTable native call `netstat -b` itself is built on. Same
/// interop-risk tier as CpuTopologyService's native calls - wrapped to return an empty list on
/// any failure rather than throwing.
/// </summary>
public static class NetworkConnectionsService
{
    private const int AfInet = 2; // IPv4 only - matches what the Network tab's other diagnostics already scope to.
    private const int TcpTableOwnerPidAll = 5;

    public static List<TcpConnectionInfo> Sample()
    {
        var results = new List<TcpConnectionInfo>();
        try
        {
            var processNames = BuildProcessNameCache();
            foreach (var row in ReadTcpTable())
            {
                results.Add(new TcpConnectionInfo(
                    new IPAddress(row.LocalAddr).ToString(), ExtractPort(row.LocalPort),
                    new IPAddress(row.RemoteAddr).ToString(), ExtractPort(row.RemotePort),
                    StateName(row.State), (int)row.OwningPid,
                    processNames.TryGetValue((int)row.OwningPid, out var name) ? name : "(unknown)"));
            }
        }
        catch
        {
            // Best-effort - an empty list just means nothing to show.
        }
        return results;
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

    /// <summary>The native struct's port fields are a DWORD but only the low 16 bits are used, in
    /// network (big-endian) byte order - swap to host order to get the real port number.</summary>
    private static int ExtractPort(uint raw) => IPAddress.NetworkToHostOrder((short)(ushort)raw) & 0xFFFF;

    private static string StateName(uint state) => state switch
    {
        1 => "CLOSED", 2 => "LISTENING", 3 => "SYN_SENT", 4 => "SYN_RCVD",
        5 => "ESTABLISHED", 6 => "FIN_WAIT1", 7 => "FIN_WAIT2", 8 => "CLOSE_WAIT",
        9 => "CLOSING", 10 => "LAST_ACK", 11 => "TIME_WAIT", 12 => "DELETE_TCB",
        _ => "UNKNOWN",
    };

    private static List<MIB_TCPROW_OWNER_PID> ReadTcpTable()
    {
        var list = new List<MIB_TCPROW_OWNER_PID>();
        int bufSize = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref bufSize, sort: true, AfInet, TcpTableOwnerPidAll, 0);
        if (bufSize <= 0) return list;

        IntPtr buffer = Marshal.AllocHGlobal(bufSize);
        try
        {
            uint result = GetExtendedTcpTable(buffer, ref bufSize, sort: true, AfInet, TcpTableOwnerPidAll, 0);
            if (result != 0) return list; // non-zero = a Win32 error, not NO_ERROR

            int numEntries = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            for (int i = 0; i < numEntries; i++)
                list.Add(Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(IntPtr.Add(rowPtr, i * rowSize)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return list;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, uint reserved);

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
    public static WifiInfo? ReadCurrentWifi()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

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
