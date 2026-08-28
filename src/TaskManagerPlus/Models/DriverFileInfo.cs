using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>
/// #459: one file installed by a driver package, via Win32_PnPSignedDriverCIMDataFile (the
/// association class linking a Win32_PnPSignedDriver to every Win32_CIMLogicalFile it installed -
/// .sys, .dll, .inf, ...). Shown in the Devices &amp; Drivers tab's detail pane when a driver row is
/// selected. SignatureStatus starts "Not checked" and is filled in per-file on demand (reusing
/// CatalogSignatureService, the same #454 catalog-aware check the main grid's Verify button uses) -
/// populating it for every file of every driver package up front would multiply an already opt-in
/// bulk check by however many files each package installs, so it stays a per-file action instead.
/// ObservableObject-backed (like DriverInventoryRow) so that per-file Verify updates live.
/// </summary>
public sealed class DriverFileInfo : ObservableObject
{
    public string FileName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string? Version { get; init; }
    public long? SizeBytes { get; init; }

    private string _signatureStatus = "Not checked";
    public string SignatureStatus { get => _signatureStatus; set => SetProperty(ref _signatureStatus, value); }
}
