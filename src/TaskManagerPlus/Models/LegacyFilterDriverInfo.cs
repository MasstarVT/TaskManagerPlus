namespace TaskManagerPlus.Models;

/// <summary>
/// #495: one legacy (pre-minifilter) file-system filter driver - a Type=2 (SERVICE_FILE_SYSTEM_DRIVER)
/// or Type=8 (SERVICE_RECOGNIZER_DRIVER) service under HKLM\SYSTEM\CurrentControlSet\Services that
/// is NOT registered as a minifilter (no \Instances subkey, and not already listed by `fltmc
/// filters`) and isn't one of the small set of well-known base/network file systems this app
/// excludes (NTFS, FAT variants, CDFS/UDFS, the SMB redirector stack, ...) - see
/// LegacyFilterDriverService's remarks for exactly what's excluded and why. IsOrphaned mirrors
/// ClassFilterEntry's orphan logic, just checked from the opposite direction (walking the services
/// list itself rather than a filter-name reference to it): a registry entry survives even after the
/// vendor's uninstaller deletes the .sys file it points to.
/// </summary>
public sealed class LegacyFilterDriverEntry
{
    public string ServiceName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? ImagePath { get; init; }
    public string StartTypeText { get; init; } = "Unknown";

    /// <summary>True when ImagePath resolved to a path but the file no longer exists on disk - a
    /// leftover registration from an uninstalled product. Null/false (never guessed true) when the
    /// path couldn't be resolved or read at all - see LegacyFilterDriverService.</summary>
    public bool IsOrphaned { get; init; }
}
