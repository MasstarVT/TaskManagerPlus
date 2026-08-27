using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>
/// On-demand traceroute (round 9, #49) to a user-entered host. Shells out to tracert.exe rather
/// than re-implementing ICMP TTL-stepping from scratch - the same "known Windows tool, not raw
/// interop" tradeoff defrag.exe/schtasks.exe/sc.exe/vssadmin.exe already take elsewhere in this
/// app, and tracert's own text output is a stable, long-standing contract. A basic hostname-shape
/// check is applied before shelling out at all, since this runs on an arbitrary user-typed string.
/// </summary>
public static class TracerouteService
{
    private static readonly Regex ValidHostRegex = new(@"^[A-Za-z0-9][A-Za-z0-9.\-:]*$", RegexOptions.Compiled);

    public static async Task<string> RunAsync(string host, CancellationToken cancellationToken = default)
    {
        host = host.Trim();
        if (host.Length == 0) return "Enter a host name or IP address first.";
        if (host.Length > 255 || !ValidHostRegex.IsMatch(host)) return "That doesn't look like a valid host name or IP address.";

        try
        {
            var psi = new ProcessStartInfo("tracert.exe", $"-d -h 20 -w 1000 {host}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "Couldn't start tracert.exe.";

            var outputTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = proc.StandardError.ReadToEndAsync(cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return "Traceroute timed out after 30 seconds (a hop count of 20 with a 1s-per-hop timeout can still run long on a lossy path).";
            }

            string output = (await outputTask) + (await errorTask);
            return string.IsNullOrWhiteSpace(output) ? "No output." : output.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Traceroute failed: {ex.Message}";
        }
    }
}
