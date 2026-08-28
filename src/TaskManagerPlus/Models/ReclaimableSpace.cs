namespace TaskManagerPlus.Models;

/// <summary>
/// #352-#361: "what's eating my disk / how do I get space back" facts, all surfaced together in
/// the Storage tab's "Reclaimable space" card - see ReclaimableSpaceService for how each is read.
/// Every type here degrades to Unknown/empty/hidden on a failed read rather than fabricating a
/// number, same as the rest of this app's WMI/registry/shell-out facts.
/// </summary>

/// <summary>#356: `dism /Online /Cleanup-Image /AnalyzeComponentStore` output - on-demand only
/// (the analyze pass itself is slow, never run on a tick). DISM's exact field set/wording has
/// drifted a little across Windows builds, so every numeric field here is best-effort parsed from
/// the raw report and left null (shown as "Unknown") rather than guessed when a line isn't
/// recognized - RawText is kept so the full report is still readable even when parsing misses a
/// field this build phrases differently.</summary>
public sealed class ComponentStoreAnalysis
{
    public bool Available { get; init; }
    public string UnavailableReason { get; init; } = string.Empty;

    public long? ActualStoreSizeBytes { get; init; }
    public long? SharedWithWindowsBytes { get; init; }
    public long? BackupsAndDisabledFeaturesBytes { get; init; }
    public long? CacheAndTempDataBytes { get; init; }
    public bool CleanupRecommended { get; init; }
    public string DateBasedCleanupNote { get; init; } = string.Empty;

    public string RawText { get; init; } = string.Empty;
}

/// <summary>#357: one reclaimable-space bucket (Windows.old, a temp folder, the Recycle Bin, ...) -
/// SizeBytes is null when the location doesn't exist or couldn't be measured (not the same as a
/// confirmed 0).</summary>
public sealed class ReclaimableSpaceItem
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public long? SizeBytes { get; init; }
}

/// <summary>#357: Storage Sense policy read from
/// HKCU\...\StorageSense\Parameters\StoragePolicy. Only the well-documented master-enable value
/// ("01") is asserted with confidence; any other numeric policy values found under the key are
/// listed as raw name/value pairs rather than this app guessing at cadence/threshold meanings it
/// isn't confident about (several of those value names aren't consistently documented across
/// Windows builds).</summary>
public sealed class StorageSensePolicyInfo
{
    public bool Available { get; init; }
    public bool Enabled { get; init; }
    public IReadOnlyList<string> RawPolicyValues { get; init; } = Array.Empty<string>();
}

/// <summary>#358: hiberfil.sys size/enabled/full-vs-reduced state, from `powercfg /a` plus
/// HKLM\SYSTEM\CurrentControlSet\Control\Power\HiberFileSizePercent.</summary>
public sealed class HibernationInfo
{
    public bool Available { get; init; }
    public string UnavailableReason { get; init; } = string.Empty;

    public bool Enabled { get; init; }
    public long? HiberFileSizeBytes { get; init; }

    /// <summary>Null when the registry value isn't set at all (Windows' own unmodified default,
    /// not necessarily "full") - never presented as a guessed percentage.</summary>
    public int? HiberFileSizePercent { get; init; }

    public string RawText { get; init; } = string.Empty;
}

/// <summary>#360: Windows Search indexer footprint - Windows.edb size, the WSearch service's
/// current state, and (best-effort - Windows doesn't expose a reliable backlog counter as a plain
/// registry DWORD on every build) an indexing backlog count.</summary>
public sealed class IndexerFootprintInfo
{
    public long? EdbSizeBytes { get; init; }
    public string ServiceStatus { get; init; } = "Unknown";
    public string ServiceStartType { get; init; } = "Unknown";

    public bool BacklogAvailable { get; init; }
    public long BacklogItemCount { get; init; }
}
