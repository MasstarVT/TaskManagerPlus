using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>
/// #420/#421: the denominators the Memory tab's nonpaged/paged pool tiles are missing - "used" on
/// its own doesn't say anything without "used out of how much."
///
/// There is no supported API that returns Windows' actual live nonpaged/paged pool ceiling: on a
/// default (auto-sized) system that ceiling isn't a fixed number the kernel exposes anywhere -
/// WinDbg's own `!vm` gets it from private symbols, not a documented NtQuerySystemInformation
/// class, and this app has no debug-symbol access to lean on. Rather than parse an undocumented
/// struct on the hope it happens to contain the right field (this app's "never fabricate" rule),
/// this reads the one genuinely documented lever that affects pool sizing -
/// HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\NonPagedPoolSize /
/// PagedPoolSize - and, only when that override is absent (the default, and by far the common
/// case), falls back to a clearly-labeled estimate based on installed RAM. Every value this
/// service returns carries an IsEstimate flag so callers/UI can say "~" and explain why, rather
/// than presenting a guess as an authoritative limit.
/// </summary>
public static class PoolLimitsService
{
    private const string MemoryManagementKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    // A NonPagedPoolSize/PagedPoolSize registry value below this is almost certainly 0 ("let
    // Windows decide", the default) or some other non-byte-count sentinel rather than a real
    // pool-size override in bytes - treated the same as "not set" instead of risking a nonsense
    // tiny "limit" being shown as real.
    private const long PlausibleOverrideFloorBytes = 1L * 1024 * 1024;

    // Windows Internals' documented behavior for auto-sized 64-bit pools: dynamically capped
    // around three-quarters of physical RAM, with a hard ceiling regardless of how much RAM is
    // installed. Presented in the UI as an approximation, not a queried fact.
    private const double EstimateRamFraction = 0.75;
    private const long NonpagedEstimateCeilingBytes = 128L * 1024 * 1024 * 1024;   // 128 GB
    private const long PagedEstimateCeilingBytes = 512L * 1024 * 1024 * 1024;      // 512 GB

    public static PoolLimits Read(long ramTotalBytes)
    {
        long? nonpagedOverrideBytes = ReadRawDword("NonPagedPoolSize") is { } npRaw && npRaw >= PlausibleOverrideFloorBytes ? npRaw : null;
        long? pagedOverrideBytes = ReadRawDword("PagedPoolSize") is { } pRaw && pRaw >= PlausibleOverrideFloorBytes ? pRaw : null;

        // #421: SessionPoolSize is documented in MB, not bytes (unlike NonPagedPoolSize/
        // PagedPoolSize above) - any nonzero value is plausible on its own terms, so this doesn't
        // share the byte-count floor check the other two use.
        long? sessionPoolMb = ReadRawDword("SessionPoolSize") is { } spRaw && spRaw > 0 ? spRaw : null;

        long nonpagedLimit = nonpagedOverrideBytes ?? EstimateBytes(ramTotalBytes, NonpagedEstimateCeilingBytes);
        long pagedLimit = pagedOverrideBytes ?? EstimateBytes(ramTotalBytes, PagedEstimateCeilingBytes);

        return new PoolLimits
        {
            NonpagedPoolLimitBytes = nonpagedLimit,
            NonpagedPoolLimitIsEstimate = nonpagedOverrideBytes is null,
            PagedPoolLimitBytes = pagedLimit,
            PagedPoolLimitIsEstimate = pagedOverrideBytes is null,
            // #421: session pool has no reliable auto-size estimate to fall back to (unlike the
            // two above, which Windows Internals documents a dynamic-sizing formula for) - shown
            // only when an explicit registry override configures it, "Unknown"/hidden otherwise
            // rather than inventing a number.
            SessionPoolLimitBytes = sessionPoolMb is { } mb ? mb * 1024L * 1024L : null,
        };
    }

    private static long EstimateBytes(long ramTotalBytes, long ceilingBytes) =>
        ramTotalBytes <= 0 ? 0 : Math.Min((long)(ramTotalBytes * EstimateRamFraction), ceilingBytes);

    /// <summary>Reads one REG_DWORD value under the Memory Management key as a plain unsigned
    /// number (no unit assumed here - callers interpret it as bytes or MB depending on which
    /// value they asked for) - null when absent, denied, zero, or not a DWORD.</summary>
    private static long? ReadRawDword(string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MemoryManagementKey);
            if (key?.GetValue(valueName) is int i && i != 0)
            {
                // REG_DWORD comes back as a signed Int32 - reinterpret the raw bits as unsigned
                // rather than comparing the signed value, so a legitimate override >= 2 GB (which
                // .NET hands back as a negative int) isn't mistaken for "not set".
                return unchecked((uint)i);
            }
        }
        catch
        {
            // Denied/missing key - treated the same as "no override configured".
        }
        return null;
    }
}

/// <summary>Result of PoolLimitsService.Read - see its remarks for what IsEstimate means and why.</summary>
public sealed class PoolLimits
{
    public long NonpagedPoolLimitBytes { get; init; }
    public bool NonpagedPoolLimitIsEstimate { get; init; }
    public long PagedPoolLimitBytes { get; init; }
    public bool PagedPoolLimitIsEstimate { get; init; }
    public long? SessionPoolLimitBytes { get; init; }
}
