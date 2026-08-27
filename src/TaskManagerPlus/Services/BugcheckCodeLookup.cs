using System.Globalization;

namespace TaskManagerPlus.Services;

/// <summary>
/// #65: bugcheck-code-to-plain-English lookup for the Stability tab's minidump/shutdown-event
/// correlation (EventLogService.ExtractBugcheckCode) - a small, explicitly non-exhaustive table of
/// the ~35 most common Windows STOP codes (the ones a home/desktop user is actually likely to hit,
/// per Microsoft's own bug-check code reference), rather than every code Windows defines. An
/// unmatched code falls back to showing the bare hex value unchanged - the same "informational, not
/// exhaustive, never replace the real value with a guess" honesty tradeoff
/// JedecManufacturerLookup already established for RAM manufacturer codes.
/// </summary>
public static class BugcheckCodeLookup
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [0x0000000A] = "IRQL_NOT_LESS_OR_EQUAL",
        [0x0000001A] = "MEMORY_MANAGEMENT",
        [0x0000001E] = "KMODE_EXCEPTION_NOT_HANDLED",
        [0x00000024] = "NTFS_FILE_SYSTEM",
        [0x0000002E] = "DATA_BUS_ERROR",
        [0x0000003B] = "SYSTEM_SERVICE_EXCEPTION",
        [0x0000003D] = "INTERRUPT_EXCEPTION_NOT_HANDLED",
        [0x00000044] = "MULTIPLE_IRP_COMPLETE_REQUESTS",
        [0x0000004E] = "PFN_LIST_CORRUPT",
        [0x00000050] = "PAGE_FAULT_IN_NONPAGED_AREA",
        [0x00000077] = "KERNEL_STACK_INPAGE_ERROR",
        [0x0000007A] = "KERNEL_DATA_INPAGE_ERROR",
        [0x0000007E] = "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED",
        [0x0000007F] = "UNEXPECTED_KERNEL_MODE_TRAP",
        [0x0000008E] = "KERNEL_MODE_EXCEPTION_NOT_HANDLED",
        [0x0000009F] = "DRIVER_POWER_STATE_FAILURE",
        [0x000000A5] = "ACPI_BIOS_ERROR",
        [0x000000BE] = "ATTEMPTED_WRITE_TO_READONLY_MEMORY",
        [0x000000C2] = "BAD_POOL_CALLER",
        [0x000000C4] = "DRIVER_VERIFIER_DETECTED_VIOLATION",
        [0x000000C5] = "DRIVER_CORRUPTED_EXPOOL",
        [0x000000CE] = "DRIVER_UNLOADED_WITHOUT_CANCELLING_PENDING_OPERATIONS",
        [0x000000D1] = "DRIVER_IRQL_NOT_LESS_OR_EQUAL",
        [0x000000D8] = "DRIVER_USED_EXCESSIVE_PTES",
        [0x000000EF] = "CRITICAL_PROCESS_DIED",
        [0x000000F4] = "CRITICAL_OBJECT_TERMINATION",
        [0x000000FC] = "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY",
        [0x000000FE] = "BUGCODE_USB_DRIVER",
        [0x00000101] = "CLOCK_WATCHDOG_TIMEOUT",
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
