namespace TaskManagerPlus.Services;

/// <summary>
/// #212: a small built-in "usually means..." hint table keyed by driver filename - the same
/// register as BugcheckCodeLookup (informational only, never a diagnosis, bare filename always
/// kept/shown unchanged when there's no match). Deliberately short: the handful of drivers that
/// show up over and over in real-world DPC/ISR latency reports, not an attempt at exhaustive
/// vendor coverage - an unmatched driver just shows no hint, not a guess.
/// </summary>
public static class KnownOffenderDriverLookup
{
    private static readonly Dictionary<string, string> Hints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ndis.sys"] = "Network stack - usually a NIC/Wi-Fi driver queuing work through it under heavy network load; check the actual vendor NIC driver too.",
        ["storport.sys"] = "Storage stack - often a slow/older AHCI, RAID, or USB storage controller driver underneath it.",
        ["acpi.sys"] = "ACPI/power management - commonly a BIOS/firmware power-state issue (C-states, ASPM/link power management) rather than the driver itself.",
        ["usbport.sys"] = "Legacy USB stack - often a specific USB peripheral, or the USB 2 host controller driver.",
        ["usbxhci.sys"] = "USB 3 (xHCI) host controller - a peripheral or the chipset's xHCI driver, similar territory to usbport.sys.",
        ["wdf01000.sys"] = "Kernel-Mode Driver Framework shim - the real culprit is almost always the specific KMDF-based driver loaded on top of it, not this file itself.",
        ["nvlddmkm.sys"] = "NVIDIA GPU kernel driver - commonly tied to aggressive power-saving states or an outdated driver version.",
        ["dxgkrnl.sys"] = "DirectX graphics kernel - usually reflects whatever GPU vendor driver is underneath it, not a bug in this file itself.",
        ["ataport.sys"] = "IDE/ATA storage port driver - typically an older or misconfigured storage controller.",
        ["hdaudbus.sys"] = "HD Audio bus driver - often paired with a specific audio codec driver (Realtek, Conexant, ...) that's the real source.",
        ["portcls.sys"] = "Windows audio port class driver - usually paired with a specific audio miniport/codec driver as the real source.",
        ["rtwlane.sys"] = "Realtek Wireless LAN - a commonly cited source of DPC latency on laptops; check for a newer vendor driver.",
        ["rt640x64.sys"] = "Realtek Ethernet (RTL8168/8111-family) - a commonly cited DPC latency source; check for a newer vendor driver.",
        ["athw8x.sys"] = "Qualcomm Atheros Wi-Fi - another commonly cited Wi-Fi latency offender; check for a newer vendor driver.",
        ["e1d68x64.sys"] = "Intel Gigabit Ethernet - can spike under heavy network load with older drivers.",
        ["tcpip.sys"] = "TCP/IP stack - usually reflects heavy network throughput rather than a driver defect.",
        ["dump_storport.sys"] = "Crash-dump storage driver stub layered on storport.sys - see storport.sys above.",
    };

    /// <summary>Looks up a plain-English "usually means..." hint by driver filename (case-
    /// insensitive). Returns null (no hint shown) for anything not in this short table.</summary>
    public static string? Hint(string? driverFileName)
    {
        if (string.IsNullOrWhiteSpace(driverFileName)) return null;
        return Hints.TryGetValue(driverFileName.Trim(), out var hint) ? hint : null;
    }
}
