namespace TaskManagerPlus.Models;

/// <summary>
/// #467: one filter driver inserted into a device setup class's (or a single device's) driver
/// stack, read from the UpperFilters/LowerFilters REG_MULTI_SZ values under
/// HKLM\SYSTEM\CurrentControlSet\Control\Class\{class-guid} (class-wide, DeviceId null) or per-device
/// under HKLM\SYSTEM\CurrentControlSet\Enum\{deviceId} (DeviceId set) - see
/// ClassFilterDriverService. ServiceExists is false when the filter names a kernel service that no
/// longer has a HKLM\SYSTEM\CurrentControlSet\Services\{name} key at all - a classic leftover from
/// an uninstalled security/virtualization/storage-filter product that can still slow down or break
/// every device in that class's stack even though the product itself is long gone.
/// </summary>
public sealed class ClassFilterEntry
{
    public string ClassGuid { get; init; } = string.Empty;
    public string ClassName { get; init; } = "Unknown";

    /// <summary>Null for a class-wide filter (Control\Class\{guid} itself); set to the device
    /// instance ID for a per-device filter (Enum\{deviceId}).</summary>
    public string? DeviceId { get; init; }

    public string ScopeText => DeviceId is null ? "Class-wide" : "Per-device";

    public string FilterName { get; init; } = string.Empty;
    public bool IsUpperFilter { get; init; }
    public string FilterKindText => IsUpperFilter ? "Upper" : "Lower";

    /// <summary>False when HKLM\SYSTEM\CurrentControlSet\Services\{FilterName} doesn't exist -
    /// a filter driver entry pointing at nothing, still inserted into (and potentially breaking)
    /// this class/device's driver stack.</summary>
    public bool ServiceExists { get; init; }
}
