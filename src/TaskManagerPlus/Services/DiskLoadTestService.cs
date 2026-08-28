using System.IO;
using System.Threading;

namespace TaskManagerPlus.Services;

/// <summary>
/// #698: the disk-load half of the combined-load soak test - repeated sequential write+read passes
/// against one dedicated scratch file under AppPaths' own Settings directory (never a real user
/// file), sized modestly (256 MB, reused across passes rather than ever-growing) so this stays a
/// load *generator*, not a MemoryVerifyTestService-style correctness check - #698's point is
/// reproducing simultaneous CPU+GPU+disk load (the PSU/VRM-fault scenario neither subsystem alone
/// triggers), not verifying disk data integrity. FileOptions.WriteThrough asks Windows to actually
/// commit each write rather than just caching it, so this is real disk I/O, not just page-cache
/// churn. The scratch file is always deleted in a finally block, including on cancellation/abort.
/// </summary>
public static class DiskLoadTestService
{
    private const int BufferBytes = 4 * 1024 * 1024;
    private const long FileSizeBytes = 256L * 1024 * 1024;

    public static async Task RunAsync(CancellationToken ct)
    {
        string path = AppPaths.GetPath("StressTest", "diskload.tmp");
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);

            var buffer = new byte[BufferBytes];
            ulong state = DeterministicWorkload.SeedFor(0);
            for (int i = 0; i + 8 <= buffer.Length; i += 8)
            {
                state = DeterministicWorkload.Step(state);
                BitConverter.TryWriteBytes(buffer.AsSpan(i, 8), state);
            }

            while (!ct.IsCancellationRequested)
            {
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.WriteThrough))
                {
                    long written = 0;
                    while (written < FileSizeBytes && !ct.IsCancellationRequested)
                    {
                        await fs.WriteAsync(buffer, ct);
                        written += buffer.Length;
                    }
                }
                if (ct.IsCancellationRequested) break;

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, BufferBytes, FileOptions.SequentialScan))
                {
                    long read = 0;
                    while (read < FileSizeBytes && !ct.IsCancellationRequested)
                    {
                        int n = await fs.ReadAsync(buffer, ct);
                        if (n <= 0) break;
                        read += n;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Stop/a safety abort.
        }
        catch
        {
            // Best-effort load generator - a failed write/read (disk full, briefly locked, ...)
            // shouldn't crash the whole combined-load run.
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort cleanup - a leftover scratch file is harmless either way */ }
        }
    }
}
