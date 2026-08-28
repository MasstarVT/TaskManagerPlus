namespace TaskManagerPlus.Models;

/// <summary>
/// #424: one loaded kernel module (driver) from NtQuerySystemInformation(SystemModuleInformation) -
/// base address, image size, path and load order, the same inventory `driverquery` and WinDbg's
/// `lm` show. FriendlyName is filled in from `driverquery /v /fo csv` when available (matched by
/// file base name), since the raw enumeration only ever gives a file path, not a display name -
/// see KernelModuleService.
/// </summary>
public sealed class KernelModuleRow
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string? FriendlyName { get; set; }
    public long BaseAddress { get; set; }
    public long ImageSizeBytes { get; set; }
    public int LoadOrderIndex { get; set; }
}
