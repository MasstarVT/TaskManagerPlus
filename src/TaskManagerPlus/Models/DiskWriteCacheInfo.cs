namespace TaskManagerPlus.Models;

/// <summary>
/// #382: write-caching / buffer-flushing / power-protection facts for one disk, from the registry
/// Device Parameters\Disk key (CacheIsPowerProtected, UserWriteCacheSetting) Windows itself
/// consults for the Device Manager "Policies" tab. Unlike most quick-flag cards in this app, a
/// not-power-protected cache with flushing disabled is named as a real data-loss risk per this
/// item's own framing - see RiskFlag/SummaryText.
/// </summary>
public sealed class DiskWriteCacheInfo
{
    public bool CacheIsPowerProtectedKnown { get; init; }
    public bool CacheIsPowerProtected { get; init; }

    public bool UserWriteCacheSettingKnown { get; init; }
    public int UserWriteCacheSettingRaw { get; init; }
    public string UserWriteCacheSettingText { get; init; } = "Not explicitly configured (Windows/driver default applies).";

    public bool RiskFlag { get; init; }
    public string SummaryText { get; init; } = string.Empty;
}
