using System.Diagnostics;
using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>Result of one on-demand throughput test - see StorageThroughputService.RunTest.</summary>
public sealed record ThroughputTestResult(bool Success, double WriteMBps, double ReadMBps, string Message);

/// <summary>
/// On-demand simple sequential read/write throughput test (round 9, #41), against a temp file on
/// the chosen volume. Deliberately NOT a real benchmark: no queue-depth sweep, no random-I/O
/// pattern, no OS-cache-bypass flags beyond FileOptions.WriteThrough for the write pass and a
/// fresh-open + FileOptions.SequentialScan for the read pass (which still leaves recently-written
/// pages to potentially be served from the OS cache rather than the physical media on a fast NVMe
/// drive) - this is one single-threaded sequential pass, good enough for a rough "does this drive
/// feel unusually slow" sanity check, nothing more. Both the code here and the UI text that
/// presents the result say so explicitly, the same "quick visual flag, not a verdict" honesty
/// tradeoff the rest of this app already takes for its heuristic checks. The temp file is capped
/// at a modest size and always deleted afterward, even on failure.
/// </summary>
public static class StorageThroughputService
{
    private const int ChunkSizeBytes = 1024 * 1024; // 1 MB write chunks

    public static async Task<ThroughputTestResult> RunTestAsync(string driveLetter, int sizeMb = 256, CancellationToken cancellationToken = default)
    {
        string root = driveLetter.TrimEnd('\\') + @"\";
        if (!Directory.Exists(root))
            return new ThroughputTestResult(false, 0, 0, "Drive not found.");

        string tempPath = Path.Combine(root, $"tmp-tmplus-throughput-{Guid.NewGuid():N}.tmp");
        try
        {
            // Don't try to write a test file bigger than the free space actually available.
            var drive = new DriveInfo(driveLetter);
            long freeBytes = drive.AvailableFreeSpace;
            long targetBytes = (long)sizeMb * 1024 * 1024;
            if (freeBytes < targetBytes + 64L * 1024 * 1024) // leave 64MB headroom
                targetBytes = Math.Max(0, freeBytes - 64L * 1024 * 1024);
            if (targetBytes < 8L * 1024 * 1024)
                return new ThroughputTestResult(false, 0, 0, "Not enough free space on this volume to run the test safely.");

            var buffer = new byte[ChunkSizeBytes];
            Random.Shared.NextBytes(buffer); // avoid an all-zero file some filesystems could sparse-optimize

            var writeSw = Stopwatch.StartNew();
            await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: ChunkSizeBytes, FileOptions.WriteThrough))
            {
                long written = 0;
                while (written < targetBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int toWrite = (int)Math.Min(ChunkSizeBytes, targetBytes - written);
                    await fs.WriteAsync(buffer.AsMemory(0, toWrite), cancellationToken);
                    written += toWrite;
                }
                await fs.FlushAsync(cancellationToken);
            }
            writeSw.Stop();

            var readSw = Stopwatch.StartNew();
            long readTotal = 0;
            await using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: ChunkSizeBytes, FileOptions.SequentialScan))
            {
                var readBuffer = new byte[ChunkSizeBytes];
                int read;
                while ((read = await fs.ReadAsync(readBuffer.AsMemory(), cancellationToken)) > 0)
                    readTotal += read;
            }
            readSw.Stop();

            double writeMBps = writeSw.Elapsed.TotalSeconds > 0 ? targetBytes / 1024.0 / 1024.0 / writeSw.Elapsed.TotalSeconds : 0;
            double readMBps = readSw.Elapsed.TotalSeconds > 0 ? readTotal / 1024.0 / 1024.0 / readSw.Elapsed.TotalSeconds : 0;

            return new ThroughputTestResult(true, writeMBps, readMBps,
                $"Write {writeMBps:0.#} MB/s, Read {readMBps:0.#} MB/s (approximate — single-threaded sequential I/O on a {targetBytes / 1024 / 1024} MB temp file, not a real benchmark)");
        }
        catch (OperationCanceledException)
        {
            return new ThroughputTestResult(false, 0, 0, "Cancelled.");
        }
        catch (Exception ex)
        {
            return new ThroughputTestResult(false, 0, 0, $"Test failed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort cleanup */ }
        }
    }
}
