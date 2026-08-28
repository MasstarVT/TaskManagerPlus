namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, item 30: NTSTATUS / structured-exception-code -> plain-English name lookup. Kept
/// standalone and independent of any bugcheck-specific code (BugcheckParameterLookup only flags
/// *which* parameter of a given stop code happens to hold one of these values - the actual
/// code->name table lives here) so a later chunk decoding application-crash exception codes
/// (Application-log crash events report the same NTSTATUS/SEH exception-code space) can reuse it
/// without depending on anything bugcheck-specific. Deliberately covers only the status/exception
/// codes that actually show up in practice as a bugcheck parameter or an unhandled application
/// exception - not the full multi-thousand-entry NTSTATUS space - the same "commonly hit, not
/// exhaustive" tradeoff BugcheckCodeLookup already uses; an unmatched code falls back to the bare
/// hex value unchanged.
/// </summary>
public static class NtStatusLookup
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [0x80000001] = "STATUS_GUARD_PAGE_VIOLATION",
        [0x80000002] = "STATUS_DATATYPE_MISALIGNMENT",
        [0x80000003] = "STATUS_BREAKPOINT",
        [0x80000004] = "STATUS_SINGLE_STEP",
        [0xC0000005] = "STATUS_ACCESS_VIOLATION",
        [0xC0000006] = "STATUS_IN_PAGE_ERROR",
        [0xC0000008] = "STATUS_INVALID_HANDLE",
        [0xC000001D] = "STATUS_ILLEGAL_INSTRUCTION",
        [0xC0000025] = "STATUS_NONCONTINUABLE_EXCEPTION",
        [0xC0000026] = "STATUS_INVALID_DISPOSITION",
        [0xC000008C] = "STATUS_ARRAY_BOUNDS_EXCEEDED",
        [0xC000008D] = "STATUS_FLOAT_DENORMAL_OPERAND",
        [0xC000008E] = "STATUS_FLOAT_DIVIDE_BY_ZERO",
        [0xC000008F] = "STATUS_FLOAT_INEXACT_RESULT",
        [0xC0000090] = "STATUS_FLOAT_INVALID_OPERATION",
        [0xC0000091] = "STATUS_FLOAT_OVERFLOW",
        [0xC0000092] = "STATUS_FLOAT_STACK_CHECK",
        [0xC0000093] = "STATUS_FLOAT_UNDERFLOW",
        [0xC0000094] = "STATUS_INTEGER_DIVIDE_BY_ZERO",
        [0xC0000095] = "STATUS_INTEGER_OVERFLOW",
        [0xC0000096] = "STATUS_PRIVILEGED_INSTRUCTION",
        [0xC00000FD] = "STATUS_STACK_OVERFLOW",
        [0xC0000135] = "STATUS_DLL_NOT_FOUND",
        [0xC0000138] = "STATUS_ORDINAL_NOT_FOUND",
        [0xC0000139] = "STATUS_ENTRYPOINT_NOT_FOUND",
        [0xC0000142] = "STATUS_DLL_INIT_FAILED",
        [0xC0000194] = "STATUS_POSSIBLE_DEADLOCK",
        [0xC0000409] = "STATUS_STACK_BUFFER_OVERRUN",
        [0xC0000420] = "STATUS_ASSERTION_FAILURE",
        [0xC0000602] = "STATUS_FAIL_FAST_EXCEPTION",
        [0x40010005] = "DBG_CONTROL_C",
        [0xC0000728] = "STATUS_INVALID_CRUNTIME_PARAMETER",
        [0xE06D7363] = "Microsoft C++ exception (0xE06D7363, \"msc\")",
    };

    /// <summary>Full "0xXXXXXXXX (STATUS_NAME)" display string - the code is always kept as-is,
    /// never replaced, so an unmatched value still shows the real code rather than disappearing
    /// behind a guess.</summary>
    public static string Describe(string? statusHex)
    {
        if (string.IsNullOrWhiteSpace(statusHex)) return "Unknown";
        if (!BugcheckHex.TryParseCode(statusHex, out var code)) return statusHex;
        return Names.TryGetValue(code, out var name) ? $"0x{code:X8} ({name})" : $"0x{code:X8}";
    }

    /// <summary>Just the plain-English name, or null when this code isn't in the table - for
    /// callers (like BugcheckDecoder) that want to append the name only when one is actually
    /// known, rather than always re-printing the hex code they already have.</summary>
    public static string? TryDescribeName(string? statusHex)
    {
        if (!BugcheckHex.TryParseCode(statusHex, out var code)) return null;
        return Names.TryGetValue(code, out var name) ? name : null;
    }
}
