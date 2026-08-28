namespace TaskManagerPlus.Models;

/// <summary>#377: which mode the boot-time SATA/storage controller is actually bound to, inferred
/// from the driver service Windows loaded for it (not a BIOS readout - see StorageControllerService
/// for why VendorRaidOrRst is deliberately not asserted as a hard "RAID" verdict).</summary>
public enum StorageControllerMode { Ahci, LegacyIde, VendorRaidOrRst, Unknown }

/// <summary>
/// Round 19, #377/#378/#383: system-wide storage-controller facts, read once at Storage-tab load
/// (controller mode/driver binding can't change without a reboot, so there's nothing to poll) - see
/// StorageControllerService.ReadControllerFacts. #379/#380/#381/#382/#384 are per-disk instead and
/// live on their own models/ViewModel properties tied to the existing SMART disk picker.
/// </summary>
public sealed class StorageControllerFacts
{
    public StorageControllerMode Mode { get; init; } = StorageControllerMode.Unknown;
    public string ModeDetailText { get; init; } = string.Empty;
    public string? BoundDriverServiceName { get; init; }

    /// <summary>#377: "worth checking BIOS/UEFI SATA mode settings" quick flag - true only when the
    /// bound driver is unambiguously a legacy PATA/IDE-compatibility driver (atapi/pciide/intelide),
    /// never inferred from silence.</summary>
    public bool LegacyIdeQuickFlag => Mode == StorageControllerMode.LegacyIde;

    // #378 - only meaningful (ShowStorAhciMsiCheck true) when Mode == Ahci, i.e. storahci is the
    // driver actually bound - reading these values for a different driver's device node wouldn't
    // mean anything.
    public bool ShowStorAhciMsiCheck { get; init; }
    public bool? MsiSupported { get; init; }
    public bool? IdlePowerEnabled { get; init; }

    // #383
    public List<StorageDriverInventoryEntry> Drivers { get; init; } = new();
    public List<string> ProblemDevices { get; init; } = new();
    public int? DiskTimeoutValueSeconds { get; init; }

    public bool Available { get; init; } = true;
    public string UnavailableReason { get; init; } = string.Empty;
}
