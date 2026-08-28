namespace TaskManagerPlus.Services;

/// <summary>
/// #640: maps the common machine-check "Error Type"/error-source hints WHEA-Logger's own
/// formatted message text uses (cache hierarchy, bus/interconnect, memory controller, TLB, ...)
/// to a short plain-English likely cause - the same "small non-exhaustive lookup table, bare value
/// kept and never replaced by a guess" pattern BugcheckCodeLookup already established for STOP
/// codes. An unmatched hint falls back to "Unknown machine-check type," not a fabricated
/// diagnosis. Explicitly a quick interpretation, not a verdict - real machine-check bank/status
/// decoding is CPU-model-specific (see Intel/AMD's own MCA architecture manuals) and well beyond
/// what a keyword match on Windows' own formatted event text can honestly claim to determine.
/// </summary>
public static class MceBankHintLookup
{
    private static readonly (string[] Hints, string Description)[] Hints =
    {
        (new[] { "cache" }, "Cache hierarchy (L1/L2/L3) error - often a bad line fetch or ECC event in on-die cache."),
        (new[] { "tlb" }, "TLB (address-translation cache) error - usually transient, occasionally an early core-degradation sign."),
        (new[] { "bus", "interconnect" }, "Bus/interconnect error - signal integrity between CPU, chipset, or memory, worth checking DIMM/CPU seating and voltages."),
        (new[] { "memory controller", "imc" }, "Memory controller error - check RAM seating, XMP/EXPO stability, and DIMM voltage before suspecting the CPU itself."),
        (new[] { "pci express", "pcie", "pci-e" }, "PCIe link error - a flaky riser/cable, a marginal slot, or a failing card on that link."),
        (new[] { "dram", "memory" }, "Memory (DRAM) error - run a memory test; frequent recurrence points at a failing module or an unstable overclock."),
    };

    /// <summary>Best-effort description from a WHEA event's parsed Error Source / category text.
    /// Case-insensitive substring match against a small set of known machine-check error-type
    /// hints; returns a generic fallback (never empty) when nothing matches.</summary>
    public static string Describe(string? errorSourceText)
    {
        if (string.IsNullOrWhiteSpace(errorSourceText))
            return "Unknown machine-check type - see raw message.";

        foreach (var (hints, description) in Hints)
        {
            if (hints.Any(h => errorSourceText.Contains(h, StringComparison.OrdinalIgnoreCase)))
                return description;
        }
        return "Unrecognized machine-check type - see raw message.";
    }
}
