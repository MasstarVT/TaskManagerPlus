namespace TaskManagerPlus.Models;

/// <summary>#658: hibernation configuration - whether it's enabled (`powercfg /a`'s own available-
/// sleep-states report, cross-checked against HibernateEnabled under
/// HKLM\SYSTEM\CurrentControlSet\Control\Power) and the on-disk hiberfil.sys size/type (full vs.
/// reduced, via that same key's HiberFileSizePercent). See HibernationService's remarks for why
/// HiberFileSizePercent's absence means "full-size", not "unknown".</summary>
public sealed class HibernationStatus
{
    /// <summary>Null when neither the powercfg report nor the registry flag could be read at all -
    /// distinct from a definite false (hibernation is present but currently turned off).</summary>
    public bool? Enabled { get; init; }

    public long? HiberfilSizeBytes { get; init; }

    /// <summary>Null when the registry key's HiberFileSizePercent value isn't present (the
    /// full-size default) or couldn't be read; otherwise the configured percentage of RAM.</summary>
    public int? ConfiguredSizePercent { get; init; }

    public bool IsReducedSize => ConfiguredSizePercent is > 0 and < 100;

    public string StatusText { get; init; } = string.Empty;
}
