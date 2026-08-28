using TaskManagerPlus.Common;

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
/// "no problem", matching Device Manager's own convention. Code 22 specifically means "disabled" -
/// #473's "disabled devices only" filter chip checks for it directly.
///
/// Extends ObservableObject (unlike most other Models/*.cs sourced-fresh-every-load classes) purely
/// for #472's IsCheckedForRemoval - the one genuinely mutable, UI-driven bit of state on this model,
/// needed so the device-tree ListBox's per-row "select for removal" checkbox can two-way bind
/// without the whole node needing to be immutable/init-only like the rest of its properties.
/// </summary>
public sealed class PnpDeviceNode : ObservableObject
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

    /// <summary>#473: problem code 22 is Device Manager's own "This device has been disabled" -
    /// CONFIGFLAG_DISABLED, set when a user or policy manually disabled the device (as opposed to
    /// every other problem code, which reflects something Windows itself couldn't sort out).</summary>
    public bool IsDisabledByUser => ConfigManagerErrorCode == 22;

    // #469's decoded name/cause/next-step text is intentionally NOT computed here - Models are
    // plain data classes with no Services dependency (see CLAUDE.md's layering), so the lookup
    // lives in ProblemCodeToDescriptionConverter instead, the same "raw value on the model, lookup
    // in a converter" shape BugcheckCodeToDescriptionConverter already uses for a different code.

    public string HardwareIdsText => HardwareIds.Length > 0 ? string.Join(", ", HardwareIds) : "None reported";

    /// <summary>#472: checked via a per-row checkbox in the device-tree view, only ever meaningful
    /// (and only ever shown in the UI) for a non-present device - "RemoveCheckedDevicesCommand"
    /// reads this directly off whatever's currently in the DeviceTree collection.</summary>
    private bool _isCheckedForRemoval;
    public bool IsCheckedForRemoval
    {
        get => _isCheckedForRemoval;
        set => SetProperty(ref _isCheckedForRemoval, value);
    }
}
