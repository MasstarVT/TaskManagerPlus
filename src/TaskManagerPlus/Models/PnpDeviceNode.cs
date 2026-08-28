namespace TaskManagerPlus.Models;

/// <summary>
/// #468: one node of the Devices &amp; Drivers tab's device-tree view (its second top-level view,
/// distinct from the driver-inventory grid - this is device-centric, grouped by PNPClass, matching
/// Device Manager's own default "Devices by type" view). Sourced from Win32_PnPEntity for present
/// devices (PnpDeviceTreeService.ListPresentAsync) and, when the #471 "show non-present devices"
/// toggle is on, from a SetupDiGetClassDevs(DIGCF_ALLCLASSES) enumeration without DIGCF_PRESENT for
/// the ones WMI doesn't surface at all (PnpDeviceTreeService.ListNonPresentAsync) - Win32_PnPEntity
/// only ever enumerates currently-present devices, so that's the one gap in this app's "prefer
/// WMI/a known API" convention that genuinely needs raw interop.
///
/// ConfigManagerErrorCode feeds #469's decoded problem-code text (ProblemCodeDecoder) - 0 means
/// "no problem", matching Device Manager's own convention.
/// </summary>
public sealed class PnpDeviceNode
{
    public string DeviceId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ClassGuid { get; init; } = string.Empty;
    public string ClassName { get; init; } = "Unknown";
    public string Manufacturer { get; init; } = "Unknown";
    public string Status { get; init; } = string.Empty;
    public int ConfigManagerErrorCode { get; init; }
    public string[] HardwareIds { get; init; } = Array.Empty<string>();

    /// <summary>The kernel service name (driver) this device is bound to, if any - the same value
    /// DriverInventoryService joins its own rows on. Null/empty for devices with no driver bound
    /// (e.g. #469's "no driver installed" problem code 28).</summary>
    public string? Service { get; init; }

    /// <summary>#471: false for a "ghost"/non-present device found only via the SetupDiGetClassDevs
    /// enumeration - true for everything Win32_PnPEntity itself returned (which only ever lists
    /// currently-present devices).</summary>
    public bool IsPresent { get; init; } = true;

    public bool HasProblem => ConfigManagerErrorCode != 0;

    // #469's decoded name/cause/next-step text is intentionally NOT computed here - Models are
    // plain data classes with no Services dependency (see CLAUDE.md's layering), so the lookup
    // lives in ProblemCodeToDescriptionConverter instead, the same "raw value on the model, lookup
    // in a converter" shape BugcheckCodeToDescriptionConverter already uses for a different code.

    public string HardwareIdsText => HardwareIds.Length > 0 ? string.Join(", ", HardwareIds) : "None reported";
}
