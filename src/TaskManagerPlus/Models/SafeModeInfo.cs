namespace TaskManagerPlus.Models;

/// <summary>Windows' own SM_CLEANBOOT values (#726) - 0 = normal boot, 1 = Safe Mode
/// ("minimal" in Windows' own terminology), 2 = Safe Mode with Networking.</summary>
public enum SafeModeLevel
{
    Normal = 0,
    Minimal = 1,
    Network = 2,
}

/// <summary>
/// #726: live safe-mode detection, read once at process startup (safe mode can't change without
/// a reboot, so there's nothing to poll - see SafeModeDetectionService.Detect). Drives a
/// persistent header strip visible on every tab, not just Startup, since safe mode changes what
/// every tab is even able to observe (most services/drivers/startup apps simply aren't loaded).
/// </summary>
public sealed class SafeModeInfo
{
    public SafeModeLevel Level { get; init; } = SafeModeLevel.Normal;

    /// <summary>The raw HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Option\OptionValue this
    /// boot's registry corroboration read - shown alongside GetSystemMetrics(SM_CLEANBOOT) for
    /// transparency, not used to override it (see SafeModeDetectionService's remarks).</summary>
    public string? RegistryOptionValue { get; init; }

    public bool IsSafeMode => Level != SafeModeLevel.Normal;

    public string BannerText => Level switch
    {
        SafeModeLevel.Minimal =>
            "Windows is running in Safe Mode. Most services, drivers, and startup apps are not loaded - what every tab in this app shows reflects this reduced environment, not your normal configuration.",
        SafeModeLevel.Network =>
            "Windows is running in Safe Mode with Networking. Most services, drivers, and startup apps are not loaded - what every tab in this app shows reflects this reduced environment, not your normal configuration.",
        _ => string.Empty,
    };
}
