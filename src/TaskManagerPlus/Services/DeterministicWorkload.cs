namespace TaskManagerPlus.Services;

/// <summary>
/// #695/#696: a linear-congruential generator (state' = state*A + C mod 2^64) plus its "jump-ahead"
/// closed form - the shared math behind both the CPU torture test's per-thread checksum (#695) and
/// the memory test's pseudorandom pattern (#696). unchecked ulong arithmetic in .NET is exactly mod
/// 2^64, the same modulus the LCG recurrence is defined over, so both the step-by-step loop AND the
/// jump-ahead formula below land on bit-identical results - that's what makes "the output checksum
/// is known in advance" literally true rather than "known because we happened to run the same loop
/// twice": the O(log N) jump-ahead composes N steps via repeated squaring (multiply-and-add only,
/// no division, so it's exact under wraparound) instead of executing them, giving a trusted
/// reference value that's cheap to compute independently of - and much faster than - the real O(N)
/// worker loop it's checking.
/// </summary>
public static class DeterministicWorkload
{
    // Knuth's MMIX LCG constants - a well-known, well-mixed 64-bit multiplier/increment pair
    // (also used by PCG), not a self-invented one; the actual constants don't matter for
    // correctness here (any odd C and any A with the right low bits would still round-trip through
    // the jump-ahead formula exactly), only that both the worker loop and the jump-ahead check use
    // the identical pair.
    public const ulong Multiplier = 6364136223846793005UL;
    public const ulong Increment = 1442695040888963407UL;

    /// <summary>One LCG step: state' = state*A + C (mod 2^64).</summary>
    public static ulong Step(ulong state) => unchecked(state * Multiplier + Increment);

    /// <summary>Per-worker seed - distinct per index so a mismatch can be attributed to a specific
    /// thread/word position rather than every worker sharing one indistinguishable sequence.</summary>
    public static ulong SeedFor(int index) => unchecked((ulong)(index + 1) * 0x9E3779B97F4A7C15UL);

    /// <summary>The state after exactly <paramref name="steps"/> applications of <see cref="Step"/>
    /// to <paramref name="seed"/> - computed in O(log steps) via binary-exponentiation-style
    /// composition of (multiply, add) pairs, not by looping <paramref name="steps"/> times.</summary>
    public static ulong Advance(ulong seed, long steps)
    {
        if (steps <= 0) return seed;

        // Compose (curMult, curPlus) - the (A, C) of "apply Step 2^k times" - via repeated
        // squaring, accumulating only the doublings selected by steps' binary representation into
        // (accMult, accPlus). Standard LCG jump-ahead/"discard" algorithm (used by e.g. PCG's
        // advance function) - multiply-and-add only, exact under ulong (mod 2^64) wraparound.
        ulong curMult = Multiplier, curPlus = Increment;
        ulong accMult = 1, accPlus = 0;
        ulong n = unchecked((ulong)steps);

        while (n > 0)
        {
            if ((n & 1) == 1)
            {
                accMult = unchecked(accMult * curMult);
                accPlus = unchecked(accPlus * curMult + curPlus);
            }
            curPlus = unchecked((curMult + 1) * curPlus);
            curMult = unchecked(curMult * curMult);
            n >>= 1;
        }

        return unchecked(accMult * seed + accPlus);
    }
}
