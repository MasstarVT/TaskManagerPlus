using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>One Storage Spaces virtual disk (#85) - health rollup for a software-RAID-style pool,
/// if the system has one configured at all (most desktops/laptops don't - see
/// StorageSpacesService's remarks for why this whole feature degrades to "not shown" rather than
/// an error on a system with no Storage Spaces pools). Round 20, #386/#387/#388 extend this with
/// the pool's member physical disks, any in-flight repair/rebuild job, and a thin-provisioning
/// over-commit warning - all read once at Storage-tab load, same tier as the rest of this card.
/// Inherits ObservableObject (rather than being wrapped by a separate "Row" type, see
/// ServiceRow/ScheduledTaskRow for the same pattern elsewhere in this app) purely for the #387
/// repair action's mutable busy/status state below - every other property here is a one-time
/// WMI fact and stays an immutable init-only property.</summary>
public sealed class StorageSpaceInfo : ObservableObject
{
    public string PoolName { get; init; } = string.Empty;
    public string VirtualDiskName { get; init; } = string.Empty;
    public string HealthStatus { get; init; } = "Unknown";
    public string OperationalStatus { get; init; } = string.Empty;
    public string ResiliencySettingName { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public bool IsHealthWarning { get; init; }

    /// <summary>Internal WMI object identifier - not shown, used only to target the #387 Repair
    /// method at the right MSFT_VirtualDisk instance.</summary>
    public string VirtualDiskObjectId { get; init; } = string.Empty;

    // ---- #386: pool membership ------------------------------------------------------------
    public List<PoolMemberDiskInfo> MemberDisks { get; init; } = new();
    public bool HasMemberDisks => MemberDisks.Count > 0;

    // ---- #387: in-flight jobs + repair action ----------------------------------------------
    public List<StorageJobInfo> ActiveJobs { get; init; } = new();
    public bool HasActiveJobs => ActiveJobs.Count > 0;

    /// <summary>True when this virtual disk's OperationalStatus includes Degraded(3) or In
    /// Service(11) - the two states MSFT_VirtualDisk.Repair is actually meant to address. Shown as
    /// a gate on the Repair button, not a promise the repair will succeed.</summary>
    public bool CanRepair { get; init; }

    private bool _isRunningRepair;
    public bool IsRunningRepair { get => _isRunningRepair; set => SetProperty(ref _isRunningRepair, value); }

    private string _repairStatusText = string.Empty;
    public string RepairStatusText { get => _repairStatusText; set => SetProperty(ref _repairStatusText, value); }

    // ---- #388: thin provisioning ------------------------------------------------------------
    public long AllocatedSizeBytes { get; init; }
    public string ProvisioningTypeText { get; init; } = "Unknown";

    /// <summary>Empty when this virtual disk's pool has no thin virtual disk at all (#388's brief:
    /// only shown when the pool is actually using thin provisioning) or when the pool isn't
    /// over-committed. See StorageSpacesService.BuildThinProvisioningWarning.</summary>
    public string ThinProvisioningWarningText { get; init; } = string.Empty;
    public bool ShowThinProvisioningWarning => ThinProvisioningWarningText.Length > 0;

    public string SizeText => Formatting.FormatBytes(SizeBytes);
    public string AllocatedSizeText => Formatting.FormatBytes(AllocatedSizeBytes);
}
