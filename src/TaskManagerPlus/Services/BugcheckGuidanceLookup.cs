namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, item 31: curated, deliberately non-authoritative "what usually causes this" notes
/// for the more common bugchecks - "quick flag, not a verdict" per CLAUDE.md. Each entry is a
/// general leaning (driver/memory/storage/GPU/overclock/...) plus, where one applies, a pointer
/// at another card already in this app (the blamed-module dossier, the WHEA card, the TDR card,
/// the Storage tab), never a diagnosis of this specific crash. Deliberately covers only the
/// smaller set of codes a home/desktop user is actually likely to hit - the same scope
/// BugcheckCodeLookup's own header comment already used for its original ~35-code table - rather
/// than every code in the now much larger #28 table; an uncovered code shows no guidance line at
/// all instead of a generic, unhelpful one.
/// </summary>
public static class BugcheckGuidanceLookup
{
    private static readonly Dictionary<uint, string> Guidance = new()
    {
        [0x0000000A] = "Usually a driver (rarely Windows itself) touching memory it shouldn't at a raised IRQL - often a recently updated/rolled-back driver, or bad RAM. Check the blamed module below, and consider a Windows Memory Diagnostic run if this repeats with no clear driver in common.",
        [0x000000D1] = "Same shape as IRQL_NOT_LESS_OR_EQUAL, but the referencing code is confirmed to be a driver rather than the kernel itself - check the blamed module below first, then that driver's vendor for an update.",
        [0x0000001E] = "An unhandled exception in kernel mode - the decoded exception code above (access violation, etc.) usually points at a specific buggy driver rather than hardware. Check the blamed module below.",
        [0x0000001A] = "Memory-manager corruption - can be a buggy driver, failing RAM, or occasionally disk corruption on the paging file's volume. Worth a Windows Memory Diagnostic run and a check of recently installed/updated drivers.",
        [0x0000003B] = "An exception inside a system-service call, almost always from a third-party driver rather than Windows itself - check the blamed module below.",
        [0x00000050] = "A driver (or rarely the kernel) referenced memory that was never valid - a very common shape for both a buggy driver and failing RAM. Check the blamed module below, and consider a memory test if this keeps recurring at different addresses with no driver in common.",
        [0x0000007A] = "A page that should have been on disk couldn't be read back in - usually a failing/disconnecting storage device or a bad sector, occasionally a flaky SATA/NVMe cable or a storage driver bug. Check the Storage tab's drive health.",
        [0x0000007E] = "A driver-mode thread hit an exception Windows couldn't handle - check the blamed module below; this is almost always a specific driver, not hardware.",
        [0x0000007B] = "Windows couldn't access the boot volume during startup - very often a BIOS/UEFI storage-controller mode change (AHCI/RAID/NVMe), a disconnected/failing boot drive, or a missing storage driver after a hardware swap.",
        [0x0000009C] = "A CPU-reported machine-check exception - hardware-level, not a driver: overheating, an unstable overclock, failing RAM, or (rarely) a genuinely failing CPU. Check Energy & Thermals for temperatures, and revert any recent overclock.",
        [0x0000009F] = "A driver failed to respond to a power-state (sleep/hibernate/shutdown) request in time - see whether this happened during sleep/resume below, and check for an update on whichever driver owns that device class (commonly storage, USB, or GPU).",
        [0x000000A5] = "The BIOS/UEFI's own ACPI tables are malformed or non-compliant with what Windows expects - a firmware/BIOS update from the motherboard or laptop vendor is the usual fix, not a Windows driver.",
        [0x000000C2] = "A driver misused the pool allocator (double-free, use-after-free, bad tag, etc.) - the pool-tag decode below, when resolved, points at the responsible driver.",
        [0x000000C4] = "Driver Verifier caught a driver violating kernel rules - this only happens with Driver Verifier enabled, and names the offending driver directly in the parameters/analysis above.",
        [0x000000C5] = "A driver corrupted a nonpaged pool allocation, most often by writing past the end of its own buffer - the pool-tag decode below, when resolved, points at the responsible driver.",
        [0x000000D8] = "A driver leaked system PTEs (virtual address space for memory mappings) until none were left - almost always one specific driver leaking slowly over a long uptime; check what's changed or been added recently.",
        [0x000000EF] = "A critical system process (csrss.exe, wininit.exe, etc.) terminated unexpectedly - often caused by third-party security/antivirus software, disk corruption, or a failing storage device rather than a driver bug.",
        [0x000000FE] = "A USB driver detected a fatal problem - try a different USB port/cable/hub, and check for a chipset or device firmware update.",
        [0x00000101] = "A CPU core stopped responding to the clock-interrupt watchdog - can be a genuinely stuck driver/hardware, an unstable overclock, or (on some laptops) an aggressive power-saving BIOS setting. Worth checking Energy & Thermals for throttling and reverting any recent overclock.",
        [0x00000109] = "Kernel code or data was found modified when it shouldn't have been - can indicate a rootkit/malware, but far more often it's incompatible/buggy driver software or, occasionally, failing RAM.",
        [0x00000116] = "A GPU driver stopped responding and Windows recovered it (Timeout Detection and Recovery) - see the TDR card below for which driver/app and the current TdrDelay settings. Usually a GPU driver update fixes this; an aggressive overclock is the other common cause.",
        [0x00000117] = "Same TDR mechanism as VIDEO_TDR_FAILURE, but the recovery itself also timed out - a more serious version of the same GPU-driver/overclock leanings.",
        [0x00000119] = "The GPU scheduler hit an internal error - usually a GPU driver bug; check for a driver update, and roll back a recent one if this started right after an update.",
        [0x00000124] = "A fatal (uncorrectable) hardware error reported through WHEA - the joined WHEA-Logger record below has far more detail than the stop code itself (often a specific CPU, memory, or PCIe device). Overheating, an unstable overclock, and failing RAM/PSU are the most common root causes.",
        [0x00000133] = "A DPC (or the system as a whole) spent too long at an elevated IRQL without yielding - see the subtype line below; an old storage-controller driver is the classic culprit for the \"whole system\" variant.",
        [0x00000139] = "The kernel's own internal consistency check (a stack cookie, list corruption, etc.) failed - can be a buggy driver corrupting kernel structures, or, in rarer cases, deliberate tampering.",
        [0x00000141] = "A single GPU engine (not the whole driver) stopped responding - the same TDR-family leanings as VIDEO_TDR_FAILURE: driver update or an overclock rollback.",
    };

    /// <summary>Null when this code has no curated note - callers should hide the guidance
    /// section entirely rather than show an empty one.</summary>
    public static string? TryGetGuidance(string? bugcheckCode)
    {
        if (!BugcheckHex.TryParseCode(bugcheckCode, out var code)) return null;
        return Guidance.TryGetValue(code, out var text) ? text : null;
    }
}
