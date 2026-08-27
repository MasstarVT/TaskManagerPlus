using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace TaskManagerPlus.Services;

/// <summary>
/// "Signed" / "Unsigned" / "Unknown" for a file path, cached per path (a signature check reads
/// the file from disk, and the same executable is often checked from more than one caller - e.g.
/// several svchost.exe processes in the Processes tab, or the same app.exe appearing both as a
/// running process and a startup item). Extracted out of ProcessMonitorService (Round 2, where
/// this logic originated) into a shared static cache rather than duplicating it for #18's startup-
/// item trust badge - a ConcurrentDictionary since callers can run this from more than one
/// background thread (ProcessMonitorService's poll timer and StartupViewModel's background scan).
///
/// Uses the legacy X509Certificate.CreateFromSignedFile check (embedded Authenticode signature
/// only - it does NOT verify the certificate chain or check revocation, and it can't see catalog
/// signatures, which many Windows system files rely on instead of an embedded one, so a small
/// number of legitimate system binaries will show as "Unsigned" here). That's a real limitation,
/// but a full WinVerifyTrust chain-and-catalog check needs native interop this app doesn't
/// otherwise take on - the same "good enough for a quick visual flag, not a security verdict"
/// tradeoff used throughout this app (outdated-driver date filtering, the CPU throttle heuristic, ...).
/// </summary>
public static class SignatureCheckService
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string GetStatus(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return "Unknown";
        if (Cache.TryGetValue(filePath, out var cached)) return cached;

        string status;
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
            status = cert is not null ? "Signed" : "Unsigned";
        }
        catch (FileNotFoundException)
        {
            status = "Unknown";
        }
        catch (UnauthorizedAccessException)
        {
            status = "Unknown";
        }
        catch
        {
            // CreateFromSignedFile throws CryptographicException for a file with no embedded
            // signature at all - the expected, common case for an unsigned binary.
            status = "Unsigned";
        }

        Cache[filePath] = status;
        return status;
    }
}
