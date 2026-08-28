using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 16, #858: joins NetworkConnectionsService's per-process TCP table with SignatureCheckService
/// to surface processes that are (a) LISTENING on a socket bound to every interface (0.0.0.0/::) or
/// (b) hold an ESTABLISHED outbound connection to a non-loopback remote address, and that are
/// unsigned or not Microsoft-signed - anything that isn't SignedEmbedded/SignedCatalog (Unsigned,
/// Unknown, UntrustedChain, Expired) is treated as flaggable here, per #858's own guidance.
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

        foreach (var conn in NetworkConnectionsService.Sample())
        {
            bool isWildcardListening = conn.State == "LISTENING" && (conn.LocalAddress == "0.0.0.0" || conn.LocalAddress == "::");
            bool isEstablishedOutbound = conn.State == "ESTABLISHED" && !IsLoopback(conn.RemoteAddress);
            if (!isWildcardListening && !isEstablishedOutbound) continue;

            string? filePath = ResolvePath(conn.Pid, pathCache);
            if (!IsFlaggable(SignatureCheckService.GetVerification(filePath))) continue;

            findings.Add(new Finding(
                conn.Pid, conn.ProcessName, isWildcardListening ? "Listening" : "Outbound",
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
        try
        {
            using var proc = Process.GetProcessById(pid);
            path = proc.MainModule?.FileName;
        }
        catch
        {
            // Exited, protected, or access denied - leave null; SignatureCheckService treats a
            // null path as Unknown, which #858 already treats as flaggable.
        }

        cache[pid] = path;
        return path;
    }
}
