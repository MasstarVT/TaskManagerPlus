using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>#452's progress callback payload - percent across the whole run (both the write and
/// verify pass of every pattern) plus a short human-readable status line.</summary>
public sealed class MemoryPatternTestProgress
{
    public double PercentComplete { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

public sealed class MemoryPatternTestResult
{
    public bool Completed { get; init; }
    public bool Passed { get; init; }
    public long BytesTested { get; init; }
    public int MismatchedChunkCount { get; init; }
    public long? FirstErrorOffset { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

/// <summary>
/// #452: a fast, in-app, no-reboot memory sanity check - allocate a user-chosen block of RAM and
/// run a walking pattern (alternating bit patterns, then all-ones/all-zeros) over it, verifying
/// every byte reads back what was written. This is explicitly a much weaker check than a boot-time
/// tool (Windows Memory Diagnostic, memtest86): it can only touch memory the OS is willing to hand
/// this one user-mode process, which is pageable, may not even be backed by the same physical RAM
/// twice in a row (the OS is free to move/page it), and is far smaller and shorter than a real
/// multi-pass boot-time sweep - it exists purely as a "worth a look" first check that needs no
/// restart, not a substitute for #448's launcher. The allocation is unmanaged (NativeMemory, not a
/// managed byte[]) specifically so a multi-gigabyte request isn't capped by .NET's ~2 GB
/// single-object limit.
/// </summary>
public static class MemoryPatternTestService
{
    // 0x55/0xAA are the classic alternating-bit "walking ones/zeros at the byte level" pair (every
    // adjacent bit pair differs, catching bit-to-bit coupling faults); 0x00/0xFF catch stuck-at
    // faults an alternating pattern alone can miss. The same four-pattern family memtest86-style
    // tools use for a quick pass, just far fewer passes/patterns than a full boot-time suite.
    private static readonly byte[] Patterns = { 0x55, 0xAA, 0x00, 0xFF };

    private const int ChunkBytes = 4 * 1024 * 1024; // progress granularity + cancellation latency

    public static Task<MemoryPatternTestResult> RunAsync(long sizeBytes, IProgress<MemoryPatternTestProgress> progress, CancellationToken token)
        => Task.Run(() => RunCore(sizeBytes, progress, token), token);

    private static unsafe MemoryPatternTestResult RunCore(long sizeBytes, IProgress<MemoryPatternTestProgress> progress, CancellationToken token)
    {
        if (sizeBytes <= 0)
            return new MemoryPatternTestResult { Completed = false, Passed = false, StatusText = "Nothing to test." };

        void* buffer;
        try
        {
            buffer = NativeMemory.Alloc((nuint)sizeBytes);
        }
        catch (OutOfMemoryException)
        {
            return new MemoryPatternTestResult { Completed = false, Passed = false, StatusText = "Couldn't allocate that much memory - try a smaller amount." };
        }
        catch (Exception ex)
        {
            return new MemoryPatternTestResult { Completed = false, Passed = false, StatusText = $"Allocation failed: {ex.Message}" };
        }

        try
        {
            int mismatchedChunks = 0;
            long? firstErrorOffset = null;

            // Two sub-passes per pattern (write, then verify) - reported together as one
            // combined percentage across all patterns so the progress bar reads as one steady run.
            double totalSteps = Patterns.Length * 2;
            int stepIndex = 0;

            foreach (byte pattern in Patterns)
            {
                WriteFill(buffer, sizeBytes, pattern, token, progress, stepIndex, totalSteps, $"Writing pattern 0x{pattern:X2}");
                stepIndex++;

                var (mismatches, firstBad) = VerifyFill(buffer, sizeBytes, pattern, token, progress, stepIndex, totalSteps, $"Verifying pattern 0x{pattern:X2}");
                stepIndex++;

                if (mismatches > 0)
                {
                    mismatchedChunks += mismatches;
                    firstErrorOffset ??= firstBad;
                }
            }

            progress.Report(new MemoryPatternTestProgress { PercentComplete = 100, StatusText = "Done." });

            return new MemoryPatternTestResult
            {
                Completed = true,
                Passed = mismatchedChunks == 0,
                BytesTested = sizeBytes,
                MismatchedChunkCount = mismatchedChunks,
                FirstErrorOffset = firstErrorOffset,
                StatusText = mismatchedChunks == 0
                    ? $"No mismatches found across {sizeBytes:N0} bytes ({Patterns.Length} patterns)."
                    : $"{mismatchedChunks} mismatching region(s) found, first at offset {firstErrorOffset:N0}.",
            };
        }
        catch (OperationCanceledException)
        {
            return new MemoryPatternTestResult { Completed = false, Passed = false, BytesTested = sizeBytes, StatusText = "Aborted." };
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static unsafe void WriteFill(void* buffer, long sizeBytes, byte pattern, CancellationToken token,
        IProgress<MemoryPatternTestProgress> progress, int stepIndex, double totalSteps, string label)
    {
        long offset = 0;
        while (offset < sizeBytes)
        {
            token.ThrowIfCancellationRequested();
            int chunk = (int)Math.Min(ChunkBytes, sizeBytes - offset);
            new Span<byte>((byte*)buffer + offset, chunk).Fill(pattern);
            offset += chunk;
            ReportProgress(progress, stepIndex, totalSteps, offset, sizeBytes, label);
        }
    }

    private static unsafe (int MismatchedChunks, long? FirstErrorOffset) VerifyFill(void* buffer, long sizeBytes, byte pattern, CancellationToken token,
        IProgress<MemoryPatternTestProgress> progress, int stepIndex, double totalSteps, string label)
    {
        int mismatches = 0;
        long? firstBad = null;
        long offset = 0;
        while (offset < sizeBytes)
        {
            token.ThrowIfCancellationRequested();
            int chunk = (int)Math.Min(ChunkBytes, sizeBytes - offset);
            int bad = new Span<byte>((byte*)buffer + offset, chunk).IndexOfAnyExcept(pattern);
            if (bad >= 0)
            {
                mismatches++;
                firstBad ??= offset + bad;
            }
            offset += chunk;
            ReportProgress(progress, stepIndex, totalSteps, offset, sizeBytes, label);
        }
        return (mismatches, firstBad);
    }

    private static void ReportProgress(IProgress<MemoryPatternTestProgress> progress, int stepIndex, double totalSteps,
        long offset, long sizeBytes, string label)
    {
        double percent = (stepIndex + (double)offset / sizeBytes) / totalSteps * 100.0;
        progress.Report(new MemoryPatternTestProgress { PercentComplete = percent, StatusText = $"{label}: {offset:N0} / {sizeBytes:N0} bytes" });
    }
}
