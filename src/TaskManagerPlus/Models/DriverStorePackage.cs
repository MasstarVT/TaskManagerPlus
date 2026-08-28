using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>
/// #479: one row of the Devices &amp; Drivers tab's "Driver store" view - one package as reported by
/// `pnputil /enum-drivers`. This is the authoritative "what's actually installed in the driver
/// store" list, distinct from #453's driver-inventory grid (which lists currently-running kernel
/// *services*, joined to whichever single package happens to be bound right now) - a driver store
/// can (and often does) hold several old versions of the same package alongside the one actually in
/// use, which is exactly what #480's staleness flag and #484's in-use mapping below are for.
///
/// ObservableObject-backed (like DriverInventoryRow/PnpDeviceNode) purely for IsCheckedForDeletion -
/// the one genuinely mutable, UI-driven bit of state, needed for #481's checkbox-multi-select
/// "Delete checked" action (mirroring PnpDeviceNode.IsCheckedForRemoval's #472 pattern).
/// </summary>
public sealed class DriverStorePackage : ObservableObject
{
    /// <summary>The published name (e.g. "oem6.inf") - the driver store's own stable identity for
    /// this package, and the argument `pnputil /delete-driver` takes.</summary>
    public string PublishedName { get; init; } = string.Empty;

    /// <summary>The vendor's original .inf filename (e.g. "prnms003.inf") - the identity #480
    /// groups on (two packages sharing this and Provider are almost certainly successive versions
    /// of the same driver), and what DriverStoreService.ComputePackageSizeBytes looks for under
    /// %windir%\System32\DriverStore\FileRepository.</summary>
    public string OriginalName { get; init; } = string.Empty;

    public string Provider { get; init; } = "Unknown";
    public string ClassName { get; init; } = "Unknown";
    public string ClassGuid { get; init; } = string.Empty;

    /// <summary>Parsed from pnputil's combined "Driver Version" field (date + version) where
    /// possible - null when the date portion couldn't be parsed (locale/format drift), in which
    /// case #480's staleness ordering falls back to DriverVersionText alone rather than guessing.</summary>
    public DateTime? DriverDate { get; init; }

    /// <summary>The raw "Driver Version" text pnputil reported (e.g. "06/21/2006 10.0.19041.1") -
    /// kept verbatim for display and as the #480 staleness tie-breaker, alongside the parsed
    /// DriverDate above.</summary>
    public string DriverVersionText { get; init; } = string.Empty;

    public string SignerName { get; init; } = "Unknown";

    /// <summary>On-disk size of this package's FileRepository folder, best-effort matched by
    /// OriginalName + the DriverVer this package's own .inf copy declares (see
    /// DriverStoreService.ComputePackageSizeBytes's remarks) - null (shown as "Unknown", never a
    /// guessed value) when the folder couldn't be uniquely identified.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>#480: true when this isn't the newest package in its OriginalName+Provider group -
    /// a candidate for cleanup, not a verdict (the newest version might itself be broken and the
    /// user genuinely wants the older one kept around - see the view's own explanatory text).</summary>
    public bool IsStale { get; set; }

    /// <summary>#484: true when at least one currently-present device's bound driver node points
    /// at this exact published INF - see DriverStoreService.ApplyInUseInfo. Drives #481's hard
    /// "refuse to offer deletion" block.</summary>
    public bool IsInUse { get; set; }

    public string InUseText { get; set; } = "Not in use by any present device";

    private bool _isCheckedForDeletion;
    /// <summary>#481: checked via a per-row checkbox in the driver store view -
    /// DeleteCheckedDriverPackagesCommand reads this directly off whatever's currently in the
    /// DriverStore collection. The checkbox itself is disabled in the view whenever IsInUse is
    /// true, but DeleteCheckedDriverPackagesAsync re-checks IsInUse before acting on any row
    /// regardless, rather than trusting the view alone.</summary>
    public bool IsCheckedForDeletion
    {
        get => _isCheckedForDeletion;
        set => SetProperty(ref _isCheckedForDeletion, value);
    }
}
