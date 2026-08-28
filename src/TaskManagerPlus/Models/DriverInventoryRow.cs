using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>
/// One row of the Devices &amp; Drivers tab's driver inventory grid (#453) - anchored on
/// `driverquery /v /fo csv` (ServiceName/DisplayName/State/StartModeText/DriverType/FilePath all
/// come from there) and enriched, where a matching entry exists, with Win32_PnPSignedDriver fields
/// (Provider/InfName/DriverVersion/DriverDate/DeviceClass/PnpDeviceId) plus registry reads for
/// kernel start type/load order (#460) and hardware-ID match quality (#458). Not every kernel
/// driver has a PnP-signed counterpart (plenty of OS-internal filter/bus drivers don't) - those
/// fields stay "Unknown"/null rather than a guessed value, same as the rest of this app.
///
/// Signature fields (#454/#455/#457) start unpopulated ("Not checked") and are filled in later,
/// per-row or via "Verify all", by CatalogSignatureService - deliberately not part of the initial
/// load since a catalog-aware WinVerifyTrust check on every driver can be slow (CLAUDE.md's
/// on-demand convention: gate expensive work behind an explicit action).
///
/// ObservableObject-backed (not a plain record like KernelModuleRow) specifically so the signature/
/// match-quality columns can update live in the DataGrid after a Verify click without a full
/// collection rebuild.
/// </summary>
public sealed class DriverInventoryRow : ObservableObject
{
    // --- from driverquery /v /fo csv (always present for every row - this is the anchor list) ---
    public string ServiceName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string DriverType { get; init; } = string.Empty;
    public string StartModeText { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;

    // --- from Win32_PnPSignedDriver, joined via the Enum\<DeviceID>\Service registry value
    // (#453) - "Unknown"/null when no PnP-signed counterpart was found for this service. ---
    public string Provider { get; init; } = "Unknown";
    public string InfName { get; init; } = string.Empty;
    public string DriverVersion { get; init; } = string.Empty;
    public DateTime? DriverDate { get; init; }
    public string DeviceClass { get; init; } = string.Empty;
    public string? PnpDeviceId { get; init; }

    // --- HKLM\SYSTEM\CurrentControlSet\Services\<name> (#460) ---
    public int? RegistryStart { get; init; }
    public int? RegistryType { get; init; }
    public string? Group { get; init; }
    public int? Tag { get; init; }

    /// <summary>Plain-English boot-start/system-start/auto-start/demand-start/disabled label built
    /// from RegistryStart, plus the load-order group when one is set - see
    /// DriverInventoryService.DescribeStartType.</summary>
    public string StartTypeText { get; init; } = "Unknown";

    /// <summary>#458: Exact / Compatible / Generic / Unknown - compares the device's own hardware
    /// IDs against the MatchingDeviceId recorded for the driver package under
    /// Control\Class\{guid}\NNNN. "Generic" flags a device quietly running on a compatible-ID
    /// fallback (Microsoft Basic Display/Storage/Audio driver, ...) instead of its real vendor
    /// driver.</summary>
    public string MatchQuality { get; init; } = "Unknown";

    // --- #461: file-version company name, for the third-party filter toggle. Unknown/empty just
    // means the filter can't classify this row and it's shown regardless (never hidden by a
    // failed read). ---
    public string? CompanyName { get; init; }
    public bool IsThirdParty { get; init; }

    // --- #454/#455/#457: populated on demand by CatalogSignatureService, either per-row (Verify)
    // or in bulk (Verify all). ---
    private string _signatureStatus = "Not checked";
    public string SignatureStatus
    {
        get => _signatureStatus;
        set
        {
            if (SetProperty(ref _signatureStatus, value)) OnPropertyChanged(nameof(IsUnsignedOrTestSigned));
        }
    }

    private string? _signerName;
    public string? SignerName { get => _signerName; set => SetProperty(ref _signerName, value); }

    private bool _isWhql;
    /// <summary>#457: true when SignerName is "Microsoft Windows Hardware Compatibility
    /// Publisher" - the WHQL signing certificate every hardware-certified driver is catalog-signed
    /// with, as opposed to a vendor's own Authenticode certificate.</summary>
    public bool IsWhql { get => _isWhql; set => SetProperty(ref _isWhql, value); }

    private bool _isCatalogSigned;
    public bool IsCatalogSigned { get => _isCatalogSigned; set => SetProperty(ref _isCatalogSigned, value); }

    /// <summary>#455: true once SignatureStatus lands on "Unsigned" or "Test-signed / untrusted
    /// root" - drives the flag column's highlight. False while still "Not checked", so an
    /// unverified row isn't shown as a false negative.</summary>
    public bool IsUnsignedOrTestSigned =>
        SignatureStatus is "Unsigned" or "Test-signed / untrusted root";
}
