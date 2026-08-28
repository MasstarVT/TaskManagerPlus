namespace TaskManagerPlus.Models;

/// <summary>
/// #351: one `fsutil behavior query &lt;setting&gt;` result. Unlike every other fact in this round,
/// these five settings are system-wide (not per volume - `fsutil behavior` has no volume concept
/// except disable8dot3's optional, separate per-volume override, not queried here), so this is read
/// once for the whole machine rather than once per VolumeFilesystemRow.
/// </summary>
public sealed class NtfsBehaviorSetting
{
    /// <summary>The literal fsutil behavior-query keyword ("disablelastaccess", "disable8dot3", ...).</summary>
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string ValueText { get; init; } = "Unknown";
    public string PerformanceImplication { get; init; } = string.Empty;

    /// <summary>True only for disablelastaccess/disable8dot3 - the two settings picked as "commonly
    /// toggled and safest to expose" a set control for; mftzone/memoryusage/encryptpagingfile stay
    /// read-only (see StorageViewModel's remarks for why).</summary>
    public bool CanToggle { get; init; }
}
