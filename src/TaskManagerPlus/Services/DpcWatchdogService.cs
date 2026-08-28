using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #204: reads the two registry values the DPC watchdog actually bugchecks on -
/// HKLM\SYSTEM\CurrentControlSet\Control\DpcWatchdogProfileOffset and \DPCTimeout - so the
/// Responsiveness tab can show "how close is this machine to a DPC_WATCHDOG_VIOLATION (0x133)"
/// rather than just a raw latency number with no context for what actually crashes the system.
/// DPCTimeout = 0 means the watchdog is disabled (Microsoft's own documented convention, most
/// commonly seen with a kernel debugger attached). Neither value is set on a stock, undebugged
/// Windows install - a missing value means "using Windows' built-in default", not an error, per
/// this app's usual "degrade to Unknown/default, never fabricate" rule.
/// </summary>
public static class DpcWatchdogService
{
    /// <summary>Windows' documented default single-DPC/ISR watchdog timeout when DPCTimeout isn't
    /// overridden in the registry - used as the headroom-bar denominator in that case.</summary>
    public const int DefaultTimeoutSeconds = 2;

    public static DpcWatchdogInfo Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            object? timeoutVal = key?.GetValue("DPCTimeout");
            int? timeout = timeoutVal is int t ? t : null;
            bool disabled = timeout == 0;

            string status = timeout is null
                ? $"DPCTimeout isn't set in the registry - using Windows' built-in default (~{DefaultTimeoutSeconds}s)."
                : disabled
                    ? "DPC watchdog is disabled on this machine (DPCTimeout = 0) - usually means a kernel debugger is attached."
                    : $"DPC watchdog timeout: {timeout} second(s).";

            return new DpcWatchdogInfo
            {
                WatchdogEnabled = !disabled,
                TimeoutValue = timeout,
                StatusText = status,
            };
        }
        catch
        {
            return new DpcWatchdogInfo
            {
                WatchdogEnabled = true,
                TimeoutValue = null,
                StatusText = "Unknown - couldn't read the DPC watchdog registry values.",
            };
        }
    }
}
