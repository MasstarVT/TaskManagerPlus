using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 16, #858: joins NetworkConnectionsService's per-process TCP table with SignatureCheckService
/// to surface processes that are (a) LISTENING on a socket bound to every interface (0.0.0.0/::) or
/// (b) hold an ESTABLISHED connection (outbound, or inbound to one of its own listeners) with a
/// non-loopback remote address, and whose image isn't validly signed by anyone - anything that
/// isn't SignedEmbedded/SignedCatalog (Unsigned, Unknown, UntrustedChain, Expired) is treated as
/// flaggable here, per #858's own guidance. A validly signed third-party binary is NOT flagged.
///
/// TCP only - NetworkConnectionsService.Sample() (the existing #21/"themed netstat -b" table) has no
/// UDP counterpart, and UDP is connectionless (no LISTENING/ESTABLISHED state to key this join off
/// of the same way), so this is a real, documented limitation rather than a full UDP join.
///
/// "Quick flag, not a verdict": an unsigned process listening or connecting out is common on a clean
/// machine too (in-house tools, open-source utilities, dev servers, ...) - this surfaces exposure
/// worth a look, not a confirmed problem.
/// </summary>
public static class UnsignedNetworkActivityService
{
    public sealed record Finding(
        int Pid, string ProcessName, string Direction,
        string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort,
        string State, string SignatureStatus);

    public static List<Finding> Scan()
    {
        var findings = new List<Finding>();
        var pathCache = new Dictionary<int, string?>();
        var connections = NetworkConnectionsService.Sample().ToList();

        // An ESTABLISHED socket on one of the owning process's own LISTENING ports was accepted by
        // that listener - a client connecting IN, not this process connecting out.
        var listeningPorts = connections
            .Where(c => c.State == "LISTENING")
            .Select(c => (c.Pid, c.LocalPort))
            .ToHashSet();

        foreach (var conn in connections)
        {
            bool isWildcardListening = conn.State == "LISTENING" && (conn.LocalAddress == "0.0.0.0" || conn.LocalAddress == "::");
            bool isEstablishedRemote = conn.State == "ESTABLISHED" && !IsLoopback(conn.RemoteAddress);
            if (!isWildcardListening && !isEstablishedRemote) continue;

            string? filePath = ResolvePath(conn.Pid, pathCache);
            if (!IsFlaggable(SignatureCheckService.GetVerification(filePath))) continue;

            string direction = isWildcardListening ? "Listening"
                : listeningPorts.Contains((conn.Pid, conn.LocalPort)) ? "Inbound"
                : "Outbound";

            findings.Add(new Finding(
                conn.Pid, conn.ProcessName, direction,
                conn.LocalAddress, conn.LocalPort, conn.RemoteAddress, conn.RemotePort, conn.State,
                SignatureCheckService.GetStatus(filePath)));
        }

        return findings
            .OrderByDescending(f => f.Direction == "Listening")
            .ThenBy(f => f.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsFlaggable(SignatureVerification verification) =>
        verification is not (SignatureVerification.SignedEmbedded or SignatureVerification.SignedCatalog);

    private static bool IsLoopback(string address) => address is "127.0.0.1" or "::1" or "0.0.0.0" or "::";

    private static string? ResolvePath(int pid, Dictionary<int, string?> cache)
    {
        if (cache.TryGetValue(pid, out var cached)) return cached;

        string? path = null;
        if (pid == 4)
        {
            // "System" has no image of its own to open - its sockets (SMB, http.sys) are the kernel's.
            path = System.IO.Path.Combine(Environment.SystemDirectory, "ntoskrnl.exe");
        }
        else
        {
            // PROCESS_QUERY_LIMITED_INFORMATION works on protected (PPL) processes such as
            // MsMpEng, where Process.MainModule's full-access open is denied.
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var buffer = new System.Text.StringBuilder(1024);
                    int size = buffer.Capacity;
                    if (QueryFullProcessImageName(handle, 0, buffer, ref size)) path = buffer.ToString();
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            // Still null (exited / access denied): SignatureCheckService treats a null path as
            // Unknown, which #858 already treats as flaggable.
        }

        cache[pid] = path;
        return path;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
