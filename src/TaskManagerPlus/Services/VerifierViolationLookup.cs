namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, item 85: Parameter 1 subcode tables for the three bugchecks Driver Verifier itself
/// raises - 0xC4 DRIVER_VERIFIER_DETECTED_VIOLATION, 0xC9 DRIVER_VERIFIER_IOMANAGER_VIOLATION, and
/// 0xE6 DRIVER_VERIFIER_DMA_VIOLATION - so a Verifier-induced crash reads as e.g. "the driver
/// attempted to free memory pool which was already freed" instead of a bare hex subcode. Slots into
/// the existing BugcheckDecoder/BugcheckDecodedInfo pipeline (item #29's per-code parameter-label
/// table already exists for these three codes' OTHER parameters; this is specifically Parameter 1's
/// own sub-meaning, which #29's plain "Subcode - type of X violation" label can't express further).
///
/// Every entry below is taken directly from Microsoft's own public Bug Check 0xC4/0xC9/0xE6
/// reference pages (each documents 0xC4 as covering roughly 150 subcodes, 0xC9 around 15 for the
/// bugcheck itself (plus a much larger table of non-bugcheck "IO SYSTEM VERIFICATION ERROR" codes
/// that this app doesn't attempt, since they never appear as this bugcheck's own Parameter 1), and
/// 0xE6 about 30) - this table covers the most common/representative subset of each rather than
/// the full list, the same "reasonably complete, not exhaustive" tradeoff BugcheckCodeLookup and
/// BugcheckParameterLookup already use elsewhere in this pipeline. An unmatched subcode falls back
/// to the bare hex value with a pointer to Microsoft's own reference, never a guess.
/// </summary>
public static class VerifierViolationLookup
{
    public static string? Describe(uint bugcheckCode, IReadOnlyList<string> parameters)
    {
        if (parameters.Count == 0 || !BugcheckHex.TryParse(parameters[0], out var raw)) return null;
        uint subcode = unchecked((uint)raw);

        var table = bugcheckCode switch
        {
            0x000000C4 => C4Subcodes,
            0x000000C9 => C9Subcodes,
            0x000000E6 => E6Subcodes,
            _ => null,
        };
        if (table is null) return null;

        string docPage = bugcheckCode switch
        {
            0x000000C4 => "Bug Check 0xC4",
            0x000000C9 => "Bug Check 0xC9",
            _ => "Bug Check 0xE6",
        };

        return table.TryGetValue(subcode, out var text)
            ? $"Verifier violation 0x{subcode:X}: {text}"
            : $"Verifier violation 0x{subcode:X}: not in this app's subcode table - see Microsoft's \"{docPage}\" reference for the full documented list.";
    }

    // 0xC4 DRIVER_VERIFIER_DETECTED_VIOLATION - representative subset across the documented range
    // (pool allocation/free misuse, IRQL violations, pool-tracking-detected leaks/overruns, MDL
    // misuse, interrupt/dispatch misuse, and the Deadlock Detection block).
    private static readonly Dictionary<uint, string> C4Subcodes = new()
    {
        [0x00] = "the driver requested a zero-byte pool allocation.",
        [0x01] = "the driver tried to allocate paged memory at an IRQL above APC_LEVEL.",
        [0x02] = "the driver tried to allocate nonpaged memory at an IRQL above DISPATCH_LEVEL.",
        [0x10] = "the driver tried to free an address that was never returned by a pool-allocation call.",
        [0x11] = "the driver tried to free paged pool at an IRQL above APC_LEVEL.",
        [0x12] = "the driver tried to free nonpaged pool at an IRQL above DISPATCH_LEVEL.",
        [0x13] = "the driver tried to free pool memory that had already been freed (double-free).",
        [0x14] = "the driver tried to free pool memory that had already been freed (double-free).",
        [0x15] = "the driver tried to free pool that still contains an active timer.",
        [0x16] = "the driver tried to free pool at a bad address, or passed invalid parameters to a memory routine.",
        [0x17] = "the driver tried to free pool that still contains an active ERESOURCE.",
        [0x30] = "the driver passed an invalid IRQL to KeRaiseIrql.",
        [0x31] = "the driver passed an invalid IRQL to KeLowerIrql.",
        [0x32] = "the driver released a spin lock at an IRQL other than DISPATCH_LEVEL (possibly a double-release).",
        [0x33] = "the driver tried to acquire a fast mutex at an IRQL above APC_LEVEL.",
        [0x35] = "the kernel released a spin lock with an IRQL that didn't match DISPATCH_LEVEL.",
        [0x3F] = "the driver referenced or dereferenced an object whose reference count was already zero.",
        [0x51] = "the driver freed memory after writing past the end of its own allocation (buffer overrun) - caught by Pool Tracking.",
        [0x52] = "the driver freed memory after writing past the end of its own allocation (buffer overrun) - caught by Pool Tracking.",
        [0x60] = "the driver unloaded without first freeing pool it had allocated - caught by Pool Tracking.",
        [0x61] = "the driver tried to allocate pool memory while it was in the process of unloading.",
        [0x62] = "the driver unloaded with unfreed pool allocations still outstanding (a leak Pool Tracking caught at unload).",
        [0x7C] = "the driver called MmUnlockPages on an MDL whose pages were never actually locked.",
        [0x7D] = "the driver called MmUnlockPages on an MDL backed by nonpaged pool, which should never be unlocked.",
        [0x81] = "the driver called MmMapLockedPages instead of MmMapLockedPagesSpecifyCache.",
        [0xB7] = "the system BIOS corrupted low physical memory during a sleep transition (a firmware bug, not a driver bug).",
        [0xC0] = "the driver called IoCallDriver with interrupts disabled.",
        [0xC1] = "a driver dispatch routine returned with interrupts still disabled.",
        [0xE0] = "a kernel routine was called with a user-mode address passed as a kernel-only parameter.",
        [0xF0] = "the driver called memcpy (or similar) with overlapping source and destination buffers.",
        [0xF5] = "the driver passed a NULL handle to ObReferenceObjectByHandle.",
        [0x1000] = "self-deadlock: the thread tried to recursively/exclusively acquire a resource it already owns only shared - Deadlock Detection.",
        [0x1001] = "deadlock: a lock-acquisition-order violation was detected - Deadlock Detection (use !deadlock in a kernel debugger for detail).",
        [0x1002] = "the driver acquired a resource that was never initialized - Deadlock Detection.",
        [0x1003] = "the driver released a resource in the wrong order relative to another held resource - Deadlock Detection.",
        [0x1004] = "a resource was released by a different thread than the one that acquired it - Deadlock Detection.",
        [0x1007] = "the driver released a resource it never acquired - Deadlock Detection.",
    };

    // 0xC9 DRIVER_VERIFIER_IOMANAGER_VIOLATION - the full documented Parameter-1 table for the
    // bugcheck itself (the larger 0x200+ "IO SYSTEM VERIFICATION ERROR" table Microsoft's page
    // also lists is a separate, non-bugcheck debugger-break mechanism, not this parameter).
    private static readonly Dictionary<uint, string> C9Subcodes = new()
    {
        [0x01] = "the driver tried to free an object whose type is not IO_TYPE_IRP.",
        [0x02] = "the driver tried to free an IRP that is still associated with a thread.",
        [0x03] = "the driver passed IoCallDriver an IRP whose type is not IRP_TYPE.",
        [0x04] = "the driver passed IoCallDriver an invalid device object.",
        [0x05] = "the IRQL changed during a call to the driver's dispatch routine.",
        [0x06] = "the driver called IoCompleteRequest with a status marked as pending (or -1).",
        [0x07] = "the driver called IoCompleteRequest while its own cancel routine was still set.",
        [0x08] = "the driver passed IoBuildAsynchronousFsdRequest an invalid buffer.",
        [0x09] = "the driver passed IoBuildDeviceIoControlRequest an invalid buffer.",
        [0x0A] = "the driver passed IoInitializeTimer a device object with an already-initialized timer.",
        [0x0C] = "the driver passed an I/O status block allocated on a stack frame that has already unwound.",
        [0x0D] = "the driver passed a user event object allocated on a stack frame that has already unwound.",
        [0x0E] = "the driver called IoCompleteRequest at an IRQL above DISPATCH_LEVEL.",
        [0x0F] = "the driver sent a create request using a file object that was already closed or had its open canceled.",
        [0x10] = "IoCallDriver was called above DISPATCH_LEVEL.",
        [0x11] = "a fast I/O dispatch routine was called above DISPATCH_LEVEL.",
        [0x12] = "a driver dispatch routine was called above DISPATCH_LEVEL.",
    };

    // 0xE6 DRIVER_VERIFIER_DMA_VIOLATION - the full documented Parameter-1 table.
    private static readonly Dictionary<uint, string> E6Subcodes = new()
    {
        [0x00] = "a miscellaneous DMA error (flushed too many bytes past the end of the map register file, or Windows ran out of contiguous map registers).",
        [0x01] = "a DMA performance counter decreased, which should never happen.",
        [0x02] = "a DMA performance counter increased too fast.",
        [0x03] = "the driver freed too many DMA common buffers - usually the same buffer freed twice.",
        [0x04] = "the driver freed too many DMA adapter channels - usually the same channel freed twice.",
        [0x05] = "the driver freed too many DMA map registers - usually the same register freed twice.",
        [0x06] = "the driver freed too many DMA scatter/gather lists - usually the same list freed twice.",
        [0x07] = "the driver released its DMA adapter without first freeing all its common buffers.",
        [0x08] = "the driver released its DMA adapter without first freeing all channels, common buffers, or scatter/gather lists.",
        [0x09] = "the driver released its DMA adapter without first freeing all map registers.",
        [0x0A] = "the driver released its DMA adapter without first freeing all scatter/gather lists.",
        [0x0B] = "the driver allocated more than one DMA adapter channel at the same time (only one is permitted per adapter).",
        [0x0C] = "the driver tried to allocate too many DMA map registers at the same time.",
        [0x0D] = "the driver did not flush its DMA adapter buffers.",
        [0x0E] = "the driver attempted a DMA transfer without locking a buffer that lives in paged memory.",
        [0x0F] = "the driver or the hardware wrote outside its allocated DMA buffer (a guard-tag/padding corruption check).",
        [0x10] = "the driver tried to free its DMA map registers while some were still mapped.",
        [0x13] = "the driver called a DMA routine at an improper IRQL.",
        [0x15] = "the driver tried to allocate too many DMA map registers.",
        [0x18] = "the driver tried a DMA operation using an adapter that had already been released.",
        [0x1B] = "the driver passed an address that is not within the bounds of the MDL it also passed.",
        [0x1D] = "the driver tried to map a DMA address range that was already mapped.",
        [0x1E] = "the driver called the obsolete HalGetAdapter instead of IoGetDmaAdapter.",
        [0x1F] = "an invalid DMA buffer was referenced - outside the MDL's bounds, or a transfer length that crosses a page boundary/isn't cache-aligned.",
        [0x21] = "the driver tried to map a zero-length DMA buffer.",
        [0x26] = "an IOMMU (hardware) detected a DMA violation.",
    };
}
