using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #696: allocates a caller-chosen byte count via Marshal.AllocHGlobal (native, unmanaged memory -
/// not a managed array, so there's no risk of the GC compacting/moving it out from under a running
/// pass, and no AllowUnsafeBlocks needed since every access goes through Marshal.ReadInt64/
/// WriteInt64), writes three patterns across the whole buffer, and reads every word back to verify
/// it. Walking-ones/walking-zeros are pure functions of word position (1UL &lt;&lt; (w % 64) and its
/// complement) - no separate "expected" buffer needed, just recompute the same formula during the
/// verify pass. The pseudorandom pattern uses DeterministicWorkload's LCG stepped sequentially from
/// the same seed in both the write pass and the (separate) verify pass - two independent
/// regenerations of the identical deterministic sequence, so a mismatch means whatever is actually
/// stored at that address no longer matches what was written, not a formula error.
///
/// StressTestViewModel decides how many bytes to test (capped below the Memory tab's own
/// RamAvailableGb figure, and skipped with an explanation when free memory is too low) - this
/// service just tests exactly the byte count it's given.
/// </summary>
public static class MemoryVerifyTestService
{
    private const int MaxReportedMismatches = 50;

    // Cancellation is checked every 2^20 words (~8 MB) - frequent enough that Stop/a safety abort
    // takes effect within a fraction of a second even on a multi-GB buffer, without the overhead of
    // checking a CancellationToken on every single word.
    private const long CancellationCheckWordMask = (1L << 20) - 1;

    public static async Task<MemoryVerifyResult> RunAsync(long bytesToTest, CancellationToken ct)
    {
        const long minTestableBytes = 8 * 1024 * 1024; // below this, there's nothing meaningful to test
        if (bytesToTest < minTestableBytes)
            return new MemoryVerifyResult { Skipped = true, SkipReason = "Free memory is too low to run a meaningful memory test." };

        bytesToTest -= bytesToTest % 8; // whole 8-byte words only

        IntPtr buffer;
        try
        {
            buffer = Marshal.AllocHGlobal((IntPtr)bytesToTest);
        }
        catch (OutOfMemoryException)
        {
            return new MemoryVerifyResult { Skipped = true, SkipReason = $"Couldn't allocate {bytesToTest / 1073741824.0:0.#} GB to test - try a smaller share." };
        }

        var sw = Stopwatch.StartNew();
        var mismatches = new List<MemoryVerifyMismatch>();
        string? fault = null;
        bool cancelled = false;
        try
        {
            await Task.Run(() =>
            {
                cancelled = !RunWalkingPattern("Walking ones", buffer, bytesToTest, invert: false, ct, mismatches);
                if (!cancelled) cancelled = !RunWalkingPattern("Walking zeros", buffer, bytesToTest, invert: true, ct, mismatches);
                if (!cancelled) cancelled = !RunPseudorandomPattern(buffer, bytesToTest, ct, mismatches);
            });
        }
        catch (Exception ex)
        {
            fault = ex.Message;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        sw.Stop();

        return new MemoryVerifyResult
        {
            Completed = fault is null && !cancelled,
            BytesTested = bytesToTest,
            Mismatches = mismatches,
            FaultMessage = fault,
            ActualDuration = sw.Elapsed,
        };
    }

    /// <summary>Returns false if cancelled partway through (either the write or the verify half).</summary>
    private static bool RunWalkingPattern(string name, IntPtr buffer, long bytesToTest, bool invert, CancellationToken ct, List<MemoryVerifyMismatch> mismatches)
    {
        long wordCount = bytesToTest / 8;

        for (long w = 0; w < wordCount; w++)
        {
            ulong pattern = 1UL << (int)(w % 64);
            if (invert) pattern = ~pattern;
            Marshal.WriteInt64(PtrAt(buffer, w * 8), unchecked((long)pattern));
            if ((w & CancellationCheckWordMask) == 0 && ct.IsCancellationRequested) return false;
        }

        for (long w = 0; w < wordCount; w++)
        {
            ulong expected = 1UL << (int)(w % 64);
            if (invert) expected = ~expected;
            ulong actual = unchecked((ulong)Marshal.ReadInt64(PtrAt(buffer, w * 8)));
            if (actual != expected && mismatches.Count < MaxReportedMismatches)
                mismatches.Add(new MemoryVerifyMismatch { PatternName = name, ByteOffset = w * 8, Expected = expected, Actual = actual });
            if ((w & CancellationCheckWordMask) == 0 && ct.IsCancellationRequested) return false;
        }

        return true;
    }

    private static bool RunPseudorandomPattern(IntPtr buffer, long bytesToTest, CancellationToken ct, List<MemoryVerifyMismatch> mismatches)
    {
        long wordCount = bytesToTest / 8;
        ulong seed = DeterministicWorkload.SeedFor(0);

        ulong writeState = seed;
        for (long w = 0; w < wordCount; w++)
        {
            writeState = DeterministicWorkload.Step(writeState);
            Marshal.WriteInt64(PtrAt(buffer, w * 8), unchecked((long)writeState));
            if ((w & CancellationCheckWordMask) == 0 && ct.IsCancellationRequested) return false;
        }

        ulong verifyState = seed;
        for (long w = 0; w < wordCount; w++)
        {
            verifyState = DeterministicWorkload.Step(verifyState);
            ulong actual = unchecked((ulong)Marshal.ReadInt64(PtrAt(buffer, w * 8)));
            if (actual != verifyState && mismatches.Count < MaxReportedMismatches)
                mismatches.Add(new MemoryVerifyMismatch { PatternName = "Pseudorandom", ByteOffset = w * 8, Expected = verifyState, Actual = actual });
            if ((w & CancellationCheckWordMask) == 0 && ct.IsCancellationRequested) return false;
        }

        return true;
    }

    private static IntPtr PtrAt(IntPtr basePtr, long byteOffset) => (IntPtr)((nint)basePtr + (nint)byteOffset);
}
