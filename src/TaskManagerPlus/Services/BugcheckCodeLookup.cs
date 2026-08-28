using System.Globalization;

namespace TaskManagerPlus.Services;

/// <summary>
/// #65: bugcheck-code-to-plain-English lookup for the Stability tab's minidump/shutdown-event
/// correlation (EventLogService.ExtractBugcheckCode) - originally a small ~35-code table of just
/// the most common Windows STOP codes; #28 extends it toward Microsoft's full documented Bug
/// Check Code Reference so a rarer code stops rendering as bare hex too, while keeping the same
/// "informational, not exhaustive, never replace the real value with a guess" honesty tradeoff
/// JedecManufacturerLookup already established for RAM manufacturer codes - a handful of the most
/// obscure/rarely-seen documented codes are still deliberately left out rather than risk a
/// mislabeled entry, so the "unmatched code falls back to the bare hex value unchanged" path
/// still matters and is still exercised.
/// </summary>
public static class BugcheckCodeLookup
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        // Core kernel-internal codes (0x1-0x5F) - almost never hit by an end user directly, but
        // documented and stable since the earliest NT releases.
        [0x00000001] = "APC_INDEX_MISMATCH",
        [0x00000002] = "DEVICE_QUEUE_NOT_BUSY",
        [0x00000003] = "INVALID_AFFINITY_SET",
        [0x00000004] = "INVALID_DATA_ACCESS_TRAP",
        [0x00000005] = "INVALID_PROCESS_ATTACH_ATTEMPT",
        [0x00000006] = "INVALID_PROCESS_DETACH_ATTEMPT",
        [0x00000007] = "INVALID_SOFTWARE_INTERRUPT",
        [0x00000008] = "IRQL_NOT_DISPATCH_LEVEL",
        [0x00000009] = "IRQL_NOT_GREATER_OR_EQUAL",
        [0x0000000A] = "IRQL_NOT_LESS_OR_EQUAL",
        [0x0000000B] = "NO_EXCEPTION_HANDLING_SUPPORT",
        [0x0000000C] = "MAXIMUM_WAIT_OBJECTS_EXCEEDED",
        [0x0000000D] = "MUTEX_LEVEL_NUMBER_VIOLATION",
        [0x0000000E] = "NO_USER_MODE_CONTEXT",
        [0x0000000F] = "SPIN_LOCK_ALREADY_OWNED",
        [0x00000010] = "SPIN_LOCK_NOT_OWNED",
        [0x00000011] = "THREAD_NOT_MUTEX_OWNER",
        [0x00000012] = "TRAP_CAUSE_UNKNOWN",
        [0x00000018] = "REFERENCE_BY_POINTER",
        [0x00000019] = "BAD_POOL_HEADER",
        [0x0000001A] = "MEMORY_MANAGEMENT",
        [0x0000001E] = "KMODE_EXCEPTION_NOT_HANDLED",
        [0x00000020] = "KERNEL_APC_PENDING_DURING_EXIT",
        [0x00000021] = "QUOTA_UNDERFLOW",
        [0x00000022] = "FILE_SYSTEM",
        [0x00000023] = "FAT_FILE_SYSTEM",
        [0x00000024] = "NTFS_FILE_SYSTEM",
        [0x00000025] = "NPFS_FILE_SYSTEM",
        [0x00000026] = "CDFS_FILE_SYSTEM",
        [0x00000027] = "RDR_FILE_SYSTEM",
        [0x00000028] = "CORRUPT_ACCESS_TOKEN",
        [0x00000029] = "SECURITY_SYSTEM",
        [0x0000002A] = "INCONSISTENT_IRP",
        [0x0000002B] = "PANIC_STACK_SWITCH",
        [0x0000002E] = "DATA_BUS_ERROR",
        [0x0000002F] = "INSTRUCTION_BUS_ERROR",
        [0x00000031] = "PHASE0_INITIALIZATION_FAILED",
        [0x00000032] = "PHASE1_INITIALIZATION_FAILED",
        [0x00000033] = "UNEXPECTED_INITIALIZATION_CALL",
        [0x00000034] = "CACHE_MANAGER",
        [0x00000035] = "NO_MORE_IRP_STACK_LOCATIONS",
        [0x00000036] = "DEVICE_REFERENCE_COUNT_NOT_ZERO",
        [0x00000039] = "SYSTEM_EXIT_OWNED_MUTEX",
        [0x0000003A] = "SYSTEM_UNWIND_PREVIOUS_USER",
        [0x0000003B] = "SYSTEM_SERVICE_EXCEPTION",
        [0x0000003C] = "INTERRUPT_UNWIND_ATTEMPTED",
        [0x0000003D] = "INTERRUPT_EXCEPTION_NOT_HANDLED",
        [0x0000003F] = "NO_MORE_SYSTEM_PTES",
        [0x00000041] = "MUST_SUCCEED_POOL_EMPTY",
        [0x00000044] = "MULTIPLE_IRP_COMPLETE_REQUESTS",
        [0x00000048] = "CANCEL_STATE_IN_COMPLETED_IRP",
        [0x00000049] = "PAGE_FAULT_WITH_INTERRUPTS_OFF",
        [0x0000004A] = "IRQL_GT_ZERO_AT_SYSTEM_SERVICE",
        [0x0000004E] = "PFN_LIST_CORRUPT",
        [0x0000004F] = "NDIS_INTERNAL_ERROR",
        [0x00000050] = "PAGE_FAULT_IN_NONPAGED_AREA",
        [0x00000051] = "REGISTRY_ERROR",
        [0x00000058] = "FTDISK_INTERNAL_ERROR",
        [0x0000005A] = "CRITICAL_SERVICE_FAILED",
        [0x0000005C] = "FT_ORPHANING",

        // File-system / boot / hardware-init codes.
        [0x00000074] = "BAD_SYSTEM_CONFIG_INFO",
        [0x00000076] = "PROCESS_HAS_LOCKED_PAGES",
        [0x00000077] = "KERNEL_STACK_INPAGE_ERROR",
        [0x00000079] = "MISMATCHED_HAL",
        [0x0000007A] = "KERNEL_DATA_INPAGE_ERROR",
        [0x0000007B] = "INACCESSIBLE_BOOT_DEVICE",
        [0x0000007E] = "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED",
        [0x0000007F] = "UNEXPECTED_KERNEL_MODE_TRAP",
        [0x00000080] = "NMI_HARDWARE_FAILURE",
        [0x00000081] = "SPIN_LOCK_INIT_FAILURE",
        [0x0000008B] = "MBR_CHECKSUM_MISMATCH",
        [0x0000008E] = "KERNEL_MODE_EXCEPTION_NOT_HANDLED",
        [0x00000093] = "INVALID_KERNEL_HANDLE",
        [0x00000094] = "KERNEL_STACK_LOCKED_AT_EXIT",
        [0x00000096] = "INVALID_WORK_QUEUE_ITEM",
        [0x00000099] = "END_OF_NT_EVALUATION_PERIOD",
        [0x0000009C] = "MACHINE_CHECK_EXCEPTION",
        [0x0000009E] = "USER_MODE_HEALTH_MONITOR",
        [0x0000009F] = "DRIVER_POWER_STATE_FAILURE",
        [0x000000A0] = "INTERNAL_POWER_ERROR",
        [0x000000A5] = "ACPI_BIOS_ERROR",
        [0x000000A7] = "ACPI_DRIVER_INTERNAL",
        [0x000000B4] = "VIDEO_DRIVER_INIT_FAILURE",
        [0x000000B8] = "ATTEMPTED_SWITCH_FROM_DPC",
        [0x000000BE] = "ATTEMPTED_WRITE_TO_READONLY_MEMORY",

        // Pool / driver-verifier / driver-misbehavior codes - the ones a debugging-tools user
        // sees most often once a driver is actually misbehaving.
        [0x000000C1] = "SPECIAL_POOL_DETECTED_MEMORY_CORRUPTION",
        [0x000000C2] = "BAD_POOL_CALLER",
        [0x000000C4] = "DRIVER_VERIFIER_DETECTED_VIOLATION",
        [0x000000C5] = "DRIVER_CORRUPTED_EXPOOL",
        [0x000000C6] = "DRIVER_CAUGHT_MODIFYING_FREED_POOL",
        [0x000000C7] = "TIMER_OR_DPC_INVALID",
        [0x000000C8] = "IRQL_UNEXPECTED_VALUE",
        [0x000000C9] = "DRIVER_VERIFIER_IOMANAGER_VIOLATION",
        [0x000000CA] = "PNP_DETECTED_FATAL_ERROR",
        [0x000000CB] = "DRIVER_LEFT_LOCKED_PAGES_IN_PROCESS",
        [0x000000CC] = "PAGE_FAULT_IN_FREED_SPECIAL_POOL",
        [0x000000CD] = "PAGE_FAULT_BEYOND_END_OF_ALLOCATION",
        [0x000000CE] = "DRIVER_UNLOADED_WITHOUT_CANCELLING_PENDING_OPERATIONS",
        [0x000000D0] = "DRIVER_CORRUPTED_MMPOOL",
        [0x000000D1] = "DRIVER_IRQL_NOT_LESS_OR_EQUAL",
        [0x000000D2] = "BUGCODE_ID_DRIVER",
        [0x000000D3] = "DRIVER_PORTION_MUST_BE_NONPAGED",
        [0x000000D5] = "DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL",
        [0x000000D6] = "DRIVER_PAGE_FAULT_BEYOND_END_OF_ALLOCATION",
        [0x000000D7] = "DRIVER_UNMAPPING_INVALID_VIEW",
        [0x000000D8] = "DRIVER_USED_EXCESSIVE_PTES",
        [0x000000D9] = "LOCKED_PAGES_TRACKER_CORRUPTION",
        [0x000000DA] = "SYSTEM_PTE_MISUSE",
        [0x000000DB] = "DRIVER_CORRUPTED_SYSPTES",
        [0x000000DC] = "DRIVER_INVALID_STACK_ACCESS",
        [0x000000DE] = "POOL_CORRUPTION_IN_FILE_AREA",
        [0x000000DF] = "IMPERSONATING_WORKER_THREAD",
        [0x000000E1] = "WORKER_THREAD_RETURNED_AT_BAD_IRQL",
        [0x000000E2] = "MANUALLY_INITIATED_CRASH",
        [0x000000E3] = "RESOURCE_NOT_OWNED",
        [0x000000E4] = "WORKER_INVALID",
        [0x000000E6] = "DRIVER_VERIFIER_DMA_VIOLATION",
        [0x000000E7] = "INVALID_FLOATING_POINT_STATE",
        [0x000000E8] = "INVALID_CANCEL_OF_FILE_OPEN",
        [0x000000E9] = "ACTIVE_EX_WORKER_THREAD_TERMINATION",
        [0x000000EA] = "THREAD_STUCK_IN_DEVICE_DRIVER",
        [0x000000ED] = "SESSION_HAS_VALID_VIEWS_ON_EXIT",
        [0x000000EF] = "CRITICAL_PROCESS_DIED",
        [0x000000F3] = "DISORDERLY_SHUTDOWN",
        [0x000000F4] = "CRITICAL_OBJECT_TERMINATION",
        [0x000000F5] = "FLTMGR_FILE_SYSTEM",
        [0x000000F6] = "PCI_VERIFIER_DETECTED_VIOLATION",
        [0x000000F7] = "DRIVER_OVERRAN_STACK_BUFFER",
        [0x000000F8] = "RAMDISK_BOOT_INITIALIZATION_FAILED",
        [0x000000FA] = "HTTP_DRIVER_CORRUPTED",
        [0x000000FC] = "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY",
        [0x000000FD] = "DIRTY_NOWRITE_PAGES_CONGESTION",
        [0x000000FE] = "BUGCODE_USB_DRIVER",

        // 0x100+ - watchdogs, AGP/video, WHEA, virtualization/secure-kernel codes.
        [0x00000101] = "CLOCK_WATCHDOG_TIMEOUT",
        [0x00000102] = "DPC_WATCHDOG_TIMEOUT",
        [0x00000103] = "MUP_FILE_SYSTEM",
        [0x00000104] = "AGP_INVALID_ACCESS",
        [0x00000105] = "AGP_GART_CORRUPTION",
        [0x00000106] = "AGP_ILLEGALLY_REPROGRAMMED",
        [0x00000109] = "CRITICAL_STRUCTURE_CORRUPTION",
        [0x0000010E] = "VIDEO_MEMORY_MANAGEMENT_INTERNAL",
        [0x00000116] = "VIDEO_TDR_FAILURE",
        [0x00000117] = "VIDEO_TDR_TIMEOUT_DETECTED",
        [0x00000119] = "VIDEO_SCHEDULER_INTERNAL_ERROR",
        [0x00000124] = "WHEA_UNCORRECTABLE_ERROR",
        [0x00000133] = "DPC_WATCHDOG_VIOLATION",
        [0x00000139] = "KERNEL_SECURITY_CHECK_FAILURE",
        [0x0000013A] = "KERNEL_MODE_HEAP_CORRUPTION",
        [0x00000141] = "VIDEO_ENGINE_TIMEOUT_DETECTED",
        [0x0000018C] = "STATUS_STACK_BUFFER_OVERRUN (kernel)",
        [0x000001CA] = "SYNTHETIC_WATCHDOG_TIMEOUT",
    };

    /// <summary>Parses a "0x...." hex string (as EventLogService.ExtractBugcheckCode formats it)
    /// and appends the plain-English name in parentheses when this table has one. The bare hex code
    /// is always kept, never replaced, so an unmatched value still shows the real code rather than
    /// disappearing behind a guess.</summary>
    public static string Describe(string? bugcheckCode)
    {
        if (string.IsNullOrWhiteSpace(bugcheckCode)) return "Unknown";

        string hex = bugcheckCode.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? bugcheckCode[2..] : bugcheckCode;
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint code))
            return bugcheckCode;

        return Names.TryGetValue(code, out var name) ? $"{bugcheckCode} ({name})" : bugcheckCode;
    }
}
