using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>Item #552's machine-wide half - `netsh int tcp show global`'s offload-relevant fields.
/// Every field defaults to "Unknown" (never guessed) when netsh can't be run or its output doesn't
/// contain the expected label, since these are English-locale field labels - same limitation every
/// other netsh-parsing service in this app (WifiDiagnosticsService, DnsConfigService, ...) already
/// documents for itself.</summary>
public sealed record TcpGlobalSettings(string ReceiveSideScalingState, string ChimneyOffloadState, string ReceiveSegmentCoalescingState);

/// <summary>
/// Item #552: the system-wide (not per-adapter) TCP offload knobs - RSS, Chimney (TCP Offload
/// Engine) and Receive Segment Coalescing state - from `netsh int tcp show global`, the standard
/// tool for reading them (per CLAUDE.md's "known tool over raw interop" convention; there's no
/// simple WMI class for these). Read-only: this app never runs `netsh int tcp set global`. Paired in
/// the Adapter health card with the per-adapter LSO/RSC/checksum/RSS keywords
/// AdapterAdvancedPropertyService.FilterOffloadRelated already reads, since a buggy driver with LSO
/// or RSC enabled can produce stalled-but-not-dead connections that look like a server problem
/// rather than a local offload bug.
/// </summary>
public static class TcpGlobalSettingsService
{
    private static readonly TcpGlobalSettings Unknown = new("Unknown", "Unknown", "Unknown");

    public static async Task<TcpGlobalSettings> ReadAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh.exe", "int tcp show global")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return Unknown;

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
                return Unknown;
            }

            string output = (await outputTask) + (await errorTask);
            return Parse(output);
        }
        catch
        {
            return Unknown;
        }
    }

    private static TcpGlobalSettings Parse(string output) => new(
        ExtractField(output, "Receive-Side Scaling State"),
        ExtractField(output, "Chimney Offload State"),
        ExtractField(output, "Receive Segment Coalescing State"));

    private static string ExtractField(string output, string label)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            int idx = line.IndexOf(':');
            if (idx < 0) continue;
            if (!line[..idx].Trim().Equals(label, StringComparison.OrdinalIgnoreCase)) continue;
            string value = line[(idx + 1)..].Trim();
            return value.Length == 0 ? "Unknown" : value;
        }
        return "Unknown";
    }
}
