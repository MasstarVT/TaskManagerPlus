using System.Diagnostics;
using System.IO;
using System.Net.Sockets;

namespace TaskManagerPlus.Services;

/// <summary>One #581 LAN throughput measurement's result - either mode.</summary>
public sealed record LanThroughputResult(string Mode, string Target, double Mbps, long BytesTransferred, double DurationSeconds, string? ErrorMessage)
{
    public bool Succeeded => ErrorMessage is null;
}

/// <summary>
/// Item #581 (suggestions.md "Throughput, bufferbloat and per-process bandwidth"): measures
/// throughput to a host on the local network, so a slow #579 internet speed-test result can be
/// separated from a slow LAN/Wi-Fi link - a classic "is it my Wi-Fi or my ISP" question the
/// internet-facing speed test alone can't answer. Two independent on-demand modes, per the item's
/// own text:
///  - a large sequential read from a chosen SMB share (the local read path Explorer itself would
///    use - no extra listener needed, just a UNC path the user already has access to);
///  - a raw TCP stream from a listener the user starts on the other machine (this app is only the
///    client side - it has no bundled server component, matching the item's own "a listener the
///    user starts" wording; any simple TCP byte-stream sender - e.g. `iperf`, `ncat -l`, or a
///    one-line script - works as the other end).
/// </summary>
public static class LanThroughputService
{
    private const int BufferSize = 1 << 20; // 1 MB reads - large enough to amortize syscall overhead over a genuinely large SMB read

    /// <summary>Reads up to <paramref name="maxBytes"/> sequentially from a local/UNC file path,
    /// timing sustained read throughput - the SMB-share half of #581.</summary>
    public static async Task<LanThroughputResult> MeasureSmbReadAsync(string filePath, long maxBytes = 200L * 1024 * 1024, CancellationToken ct = default)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
            var buffer = new byte[BufferSize];
            long total = 0;
            var sw = Stopwatch.StartNew();
            int read;
            while (total < maxBytes && (read = await fs.ReadAsync(buffer, ct)) > 0)
                total += read;
            sw.Stop();

            double seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            double mbps = total * 8.0 / seconds / 1_000_000.0;
            return new LanThroughputResult("SMB read", filePath, mbps, total, seconds, null);
        }
        catch (Exception ex)
        {
            return new LanThroughputResult("SMB read", filePath, 0, 0, 0, ex.Message);
        }
    }

    /// <summary>Connects to a raw TCP listener at <paramref name="hostPort"/> ("host:port") and
    /// reads whatever it sends for up to <paramref name="duration"/>, timing throughput - the
    /// "raw TCP stream to a listener on the other machine" half of #581.</summary>
    public static async Task<LanThroughputResult> MeasureTcpAsync(string hostPort, TimeSpan duration, CancellationToken ct = default)
    {
        try
        {
            int colonIndex = hostPort.LastIndexOf(':');
            if (colonIndex <= 0 || !int.TryParse(hostPort[(colonIndex + 1)..], out int port))
                return new LanThroughputResult("TCP stream", hostPort, 0, 0, 0, "Enter the target as host:port (e.g. 192.168.1.20:5201).");
            string host = hostPort[..colonIndex];

            using var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, connectCts.Token);

            using var stream = client.GetStream();
            var buffer = new byte[1 << 16];
            long total = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(TimeSpan.FromSeconds(3));
                int read;
                try { read = await stream.ReadAsync(buffer, readCts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; } // listener went quiet - stop and report what we got
                if (read <= 0) break;
                total += read;
            }
            sw.Stop();

            double seconds = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            double mbps = total * 8.0 / seconds / 1_000_000.0;
            if (total == 0) return new LanThroughputResult("TCP stream", hostPort, 0, 0, 0, "Connected, but no data was received - check the listener is actually sending.");
            return new LanThroughputResult("TCP stream", hostPort, mbps, total, seconds, null);
        }
        catch (Exception ex)
        {
            return new LanThroughputResult("TCP stream", hostPort, 0, 0, 0, ex.Message);
        }
    }
}
