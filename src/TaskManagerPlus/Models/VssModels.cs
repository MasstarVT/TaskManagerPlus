using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>#394: one VSS writer's health, from `vssadmin list writers`. "Stable" state (code 1)
/// combined with a "No error" last-error is the only healthy combination - anything else (Failed,
/// a Failed-at-* sub-state, a non-"No error" last-error text, ...) is flagged. A failed writer is
/// the single most common root cause of "my backup keeps failing," and it's otherwise invisible
/// anywhere else in Windows. See VssService.ReadWritersAsync.</summary>
public sealed class VssWriterInfo
{
    public string Name { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public int StateCode { get; set; } = -1;
    public string StateText { get; set; } = "Unknown";
    public string LastError { get; set; } = "Unknown";

    public bool IsHealthy => StateCode == 1 && LastError.Equals("No error", StringComparison.OrdinalIgnoreCase);
    public string StatusSummaryText => StateCode >= 0 ? $"[{StateCode}] {StateText}" : StateText;
}

/// <summary>#395/#399: one correlated VSS/SPP/volsnap failure event - Application-log "VSS"
/// (8193, 12289, 12293) and "SPP" (16387), System-log "volsnap" (25, 33, 36). A writer that
/// currently reports "Stable" but has been failing nightly is still visible here. volsnap 25 is
/// specifically why restore points/shadow copies silently disappear (the shadow-copy storage area
/// couldn't grow in time) - ExplanationText calls that out distinctly rather than leaving it lost
/// in a generic list, per #399's brief. See VssService.ReadRelatedEvents.</summary>
public sealed class VssRelatedEventInfo
{
    public DateTime TimeCreated { get; set; }
    public string Source { get; set; } = string.Empty; // "VSS" / "SPP" / "volsnap"
    public int EventId { get; set; }
    public string Volume { get; set; } = "Unknown volume";
    public string Message { get; set; } = string.Empty;

    /// <summary>#399: only volsnap 25/33/36 get an explanation - the VSS/SPP events above are
    /// left to their own FormatDescription text, which is already specific enough.</summary>
    public string ExplanationText => EventId switch
    {
        25 => "This is a common reason restore points/shadow copies silently disappear: Windows deleted existing shadow copies on this volume because the shadow-copy storage area couldn't grow in time. See the Maximum Shadow Copy Storage figure for this volume below.",
        33 or 36 => "Another volsnap shadow-copy-storage growth/capacity event on this volume - see the shadow storage limit below and the message text above for detail.",
        _ => string.Empty,
    };
    public bool HasExplanation => ExplanationText.Length > 0;
}

/// <summary>#396: one shadow copy (point-in-time snapshot), from `vssadmin list shadows` -
/// rendered grouped by volume with age, per this item's brief. Extends
/// VolumeDiagnosticsService.ReadShadowCopyUsageByVolumeAsync's aggregate-bytes-used figure (#42)
/// into "what those bytes actually are." See VssService.ReadShadowCopiesAsync.</summary>
public sealed class VssShadowCopyInfo
{
    public string Volume { get; set; } = "Unknown";
    public string ShadowCopyId { get; set; } = string.Empty;
    public string ShadowCopySetId { get; set; } = string.Empty;
    public DateTime? CreationTime { get; set; }
    public string OriginatingMachine { get; set; } = string.Empty;
    public string ServiceMachine { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ShadowCopyVolume { get; set; } = string.Empty;

    public string AgeText
    {
        get
        {
            if (CreationTime is not { } t) return "Unknown age";
            var age = DateTime.Now - t;
            if (age.TotalSeconds < 0) return "just now";
            if (age.TotalMinutes < 1) return "just now";
            if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} minute(s) ago";
            if (age.TotalDays < 1) return $"{(int)age.TotalHours} hour(s) ago";
            return $"{(int)age.TotalDays} day(s) ago";
        }
    }
}

/// <summary>#397: one volume's shadow-storage allocation/limit, from `vssadmin list
/// shadowstorage` - the SAME command VolumeDiagnosticsService (#42) already parsed for just the
/// Used figure; this extends that parse to also carry Allocated/Maximum rather than shelling out
/// twice (see VssService.ReadShadowStorageAsync -
/// VolumeDiagnosticsService.ReadShadowCopyUsageByVolumeAsync now delegates to it, unchanged for
/// its existing caller). Inherits ObservableObject purely for the resize action's mutable
/// input/busy/status state below - same "model owns its own mutable UI state" shape
/// StorageSpaceInfo's repair action already uses.</summary>
public sealed class VssShadowStorageInfo : ObservableObject
{
    /// <summary>The protected ("For") volume.</summary>
    public string Volume { get; set; } = string.Empty;

    /// <summary>Where the diff area actually lives - usually the same volume, but can be a
    /// different one.</summary>
    public string StorageVolume { get; set; } = string.Empty;

    public long UsedBytes { get; set; }
    public long AllocatedBytes { get; set; }

    /// <summary>Null unless a numeric maximum was actually parsed. IsUnbounded is the UI-facing
    /// flag for "vssadmin reported UNBOUNDED" - the actionable case (unbounded growth risk) - kept
    /// separate from "this line simply couldn't be parsed" so neither is misrepresented as the
    /// other.</summary>
    public long? MaximumBytes { get; set; }
    public bool IsUnbounded { get; set; }
    public bool HasNumericMaximum => MaximumBytes.HasValue;

    public double? UsedPercentOfMaximum => MaximumBytes is > 0
        ? Math.Min(100.0, (double)UsedBytes / MaximumBytes.Value * 100.0)
        : null;

    // ---- #397: resize action - mutable per-row UI state ---------------------------------------
    private string _newMaxSizeGb = string.Empty;
    public string NewMaxSizeGb { get => _newMaxSizeGb; set => SetProperty(ref _newMaxSizeGb, value); }

    private bool _isResizing;
    public bool IsResizing { get => _isResizing; set => SetProperty(ref _isResizing, value); }

    private string _resizeStatusText = string.Empty;
    public string ResizeStatusText { get => _resizeStatusText; set => SetProperty(ref _resizeStatusText, value); }
}

// #398's RestorePointInfo (one System Restore point, from WMI `SystemRestore` root\default) lives
// in Models/RecoveryModels.cs - RestorePointService (item 98) independently built the same reader
// against the same WMI class first; unified onto that one model rather than duplicated here.

/// <summary>#398: System Protection's per-drive enabled state. Primary detection reads the
/// SPP\Clients registry value Windows itself uses to track which volumes have System Restore's
/// client enabled (undocumented but well-established; not exposed through any WMI class or CLI
/// tool). When that read isn't available/conclusive for a given drive on this system, falls back
/// to the proxy this item's own brief sanctions: a volume with an active shadow-copy-storage
/// association (#397) is treated as "protection appears enabled." DetectionMethodText always
/// states which of the two was actually used for this row - an empty restore-point list on a
/// drive the user believes is protected is itself the finding, per this item's brief. See
/// VssService.ReadSystemProtectionStatus.</summary>
public sealed class SystemProtectionDriveStatus
{
    public string DriveLetter { get; set; } = string.Empty;
    public bool IsProtected { get; set; }
    public string DetectionMethodText { get; set; } = string.Empty;

    public string ProtectedText => IsProtected ? "Protected" : "Not protected";
}

/// <summary>#400: one registered VSS provider, from `vssadmin list providers`. A non-Microsoft
/// provider isn't a fault by itself - third-party backup/imaging software commonly installs one -
/// but it's "worth a manual check" when writers are failing or snapshots are getting stuck, since
/// a shadowing third-party provider is a recurring root cause. Quick flag, not a verdict. See
/// VssService.ReadProvidersAsync.</summary>
public sealed class VssProviderInfo
{
    public string Name { get; set; } = string.Empty;
    public string TypeText { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    public bool IsMicrosoft => Name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
}
