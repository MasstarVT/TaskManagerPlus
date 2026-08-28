using System.Diagnostics;

namespace TaskManagerPlus.Services;

/// <summary>Item #552's machine-wide half plus #565's fuller audit - every offload/tuning field
/// `netsh int tcp show global` prints that this app has a use for. Every field defaults to
/// "Unknown" (never guessed) when netsh can't be run or its output doesn't contain the expected
/// label, since these are English-locale field labels - same limitation every other netsh-parsing
/// service in this app (WifiDiagnosticsService, DnsConfigService, ...) already documents for itself.
/// Some fields (Chimney Offload State chief among them) have been dropped from `netsh`'s own output
/// on newer Windows builds - "Unknown" there just means this particular Windows version no longer
/// reports it, not a read failure.</summary>
public sealed record TcpGlobalSettings(
    string ReceiveSideScalingState,
    string ChimneyOffloadState,
    string ReceiveSegmentCoalescingState,
    string ReceiveWindowAutoTuningLevel,
    string CongestionProvider,
    string EcnCapability,
    string TimestampsState)
{
    /// <summary>#565: flags the single most common "why is my connection slow on a high-latency
    /// link" self-inflicted misconfiguration - autotuning set to disabled/restricted caps the TCP
    /// receive window, which caps throughput regardless of how much bandwidth is actually
    /// available. A very common "optimization guide" edit, per this item's own text - worded as a
    /// flag on a known-bad setting, not a guess about this machine's actual throughput.</summary>
    public bool IsAutoTuningLimited =>
        ReceiveWindowAutoTuningLevel.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
        ReceiveWindowAutoTuningLevel.Equals("restricted", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Items #552/#565: the system-wide (not per-adapter) TCP tuning/offload knobs from `netsh int tcp
/// show global`, the standard tool for reading them (per CLAUDE.md's "known tool over raw interop"
/// convention; there's no simple WMI class for these). Read-only: this app never runs `netsh int
/// tcp set global`. #552's original three fields (RSS, Chimney, RSC) are paired in the Adapter
/// health card with the per-adapter LSO/RSC/checksum/RSS keywords
/// AdapterAdvancedPropertyService.FilterOffloadRelated already reads, since a buggy driver with LSO
/// or RSC enabled can produce stalled-but-not-dead connections that look like a server problem
/// rather than a local offload bug. #565 extends the same single parse with the receive-window
/// autotuning level, congestion-control provider, ECN capability, and RFC 1323 timestamps state,
/// surfaced in full (with a one-line explanation of each) on the new TCP health card - reusing this
/// one parse rather than shelling out to the same command a second time.
/// </summary>
public static class TcpGlobalSettingsService
{
    private static readonly TcpGlobalSettings Unknown = new("Unknown", "Unknown", "Unknown", "Unknown", "Unknown", "Unknown", "Unknown");

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
        ExtractField(output, "Receive Segment Coalescing State"),
        ExtractField(output, "Receive Window Auto-Tuning Level"),
        ExtractField(output, "Add-On Congestion Control Provider"),
        ExtractField(output, "ECN Capability"),
        ExtractField(output, "RFC 1323 Timestamps"));

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
