namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, item 37: classifies a raw bugcheck fault/referenced address (0xA/0xD1/0x50's first
/// parameter) into a plain category instead of leaving it as sixteen hex digits - "quick flag,
/// not a verdict" per CLAUDE.md: plain range/byte-pattern checks against the well-known, stable
/// x64 canonical address-space split, not a real inspection of this machine's own memory layout.
/// </summary>
public static class FaultAddressClassifier
{
    // x64 canonical address space: user-mode is 0x0000000000000000-0x00007FFFFFFFFFFF, kernel-
    // mode is 0xFFFF800000000000 and up - the well-known, stable split every x64 Windows build
    // uses (addresses in between are "non-canonical" and never valid on real hardware at all).
    private const ulong KernelSpaceStart = 0xFFFF800000000000UL;
    private const ulong UserSpaceEnd = 0x00007FFFFFFFFFFFUL;
    private const ulong NearNullThreshold = 0x10000UL; // first 64KB - Windows never maps this

    /// <summary>Null when the string couldn't be parsed as a hex address at all (not applicable,
    /// as opposed to a real "unclassifiable" result).</summary>
    public static string? Classify(string? addressHex)
    {
        if (!BugcheckHex.TryParse(addressHex, out var addr)) return null;
        return $"Address classification: {Describe(addr)}. Heuristic, not a confirmed diagnosis.";
    }

    private static string Describe(ulong addr)
    {
        if (addr == 0) return "null pointer dereference";
        if (addr < NearNullThreshold) return $"near-null pointer dereference (0x{addr:X}, a small offset from a null pointer - e.g. reading a member of a null struct/object)";
        if (IsPoisonPattern(addr)) return $"looks like a freed/poisoned-memory pattern (0x{addr:X}, a repeating byte pattern) rather than a real address - a common sign of a use-after-free";
        if (addr <= UserSpaceEnd) return "in the user-mode address range - kernel/driver code read or wrote a user-mode address it shouldn't have touched directly";
        if (addr >= KernelSpaceStart) return "in the canonical kernel-mode address range - a normal-looking (if invalid) kernel pointer";
        return "in the non-canonical gap between user and kernel address space - never a valid address on real x64 hardware, itself a sign of memory corruption";
    }

    /// <summary>Windows/debug builds commonly poison freed memory with a recognizable repeating
    /// pattern - a run of one identical byte (0xCCCCCCCCCCCCCCCC, 0xFFFFFFFFFFFFFFFF, ...) or the
    /// pool allocator's own 2-byte 0xFEEE "freed" pattern repeated across the whole value. Neither
    /// is a real pointer if it shows up as a fault address, and both are a strong sign of a
    /// use-after-free rather than a fresh corruption.</summary>
    private static bool IsPoisonPattern(ulong addr)
    {
        var bytes = BitConverter.GetBytes(addr);
        byte first = bytes[0];
        if (bytes.All(b => b == first)) return true;

        ushort pair = (ushort)(addr & 0xFFFF);
        if (pair != 0xFEEE) return false;
        for (int shift = 16; shift < 64; shift += 16)
            if ((ushort)((addr >> shift) & 0xFFFF) != pair) return false;
        return true;
    }
}
