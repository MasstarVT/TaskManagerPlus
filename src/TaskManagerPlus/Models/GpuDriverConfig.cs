namespace TaskManagerPlus.Models;

/// <summary>#671: TDR (Timeout Detection and Recovery) registry configuration, read from
/// HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers - see GpuRegistryService.ReadTdrSettings.
/// Every field is nullable and null means "not present in the registry", which is itself meaningful
/// here: Windows applies its own documented default in that case, so an absent value is "using the
/// stock default," not "unknown." The Is*NonDefault flags compare only when a value *is* present.</summary>
public sealed class GpuTdrRegistrySettings
{
    // Windows' own documented defaults (Windows Hardware Dev Center, "Timeout Detection and
    // Recovery (TDR)") - shown alongside each read value so "raising TdrDelay to mask a failing
    // GPU" is visible as a deviation, not just a number with no baseline to compare against.
    public const int DefaultTdrLevel = 3; // TdrLevelRecover - attempt recovery
    public const int DefaultTdrDelaySeconds = 2;
    public const int DefaultTdrDdiDelaySeconds = 5;
    public const int DefaultTdrLimitCount = 5;
    public const int DefaultTdrLimitTimeSeconds = 60;

    public int? TdrLevel { get; init; }
    public int? TdrDelaySeconds { get; init; }
    public int? TdrDdiDelaySeconds { get; init; }
    public int? TdrLimitCount { get; init; }
    public int? TdrLimitTimeSeconds { get; init; }

    public bool TdrLevelNonDefault => TdrLevel is { } v && v != DefaultTdrLevel;
    public bool TdrDelayNonDefault => TdrDelaySeconds is { } v && v != DefaultTdrDelaySeconds;
    public bool TdrDdiDelayNonDefault => TdrDdiDelaySeconds is { } v && v != DefaultTdrDdiDelaySeconds;
    public bool TdrLimitCountNonDefault => TdrLimitCount is { } v && v != DefaultTdrLimitCount;
    public bool TdrLimitTimeNonDefault => TdrLimitTimeSeconds is { } v && v != DefaultTdrLimitTimeSeconds;

    /// <summary>#671: the specific, widely-copied "fix" this readout exists to surface - TdrDelay
    /// raised well past its 2-second default (forum advice for "my game keeps crashing with a TDR")
    /// masks a failing/overclocked GPU by giving the driver more rope before Windows kills it,
    /// rather than addressing why the GPU stopped responding in the first place.</summary>
    public bool LooksLikeMaskingFix => TdrDelaySeconds is { } d && d >= 8;

    public bool AnyNonDefault => TdrLevelNonDefault || TdrDelayNonDefault || TdrDdiDelayNonDefault ||
                                  TdrLimitCountNonDefault || TdrLimitTimeNonDefault;
}

/// <summary>#672: Hardware-accelerated GPU Scheduling (HAGS) state - HwSchMode from the same
/// GraphicsDrivers registry key. Microsoft's own documented values are 1 (disabled) and 2 (enabled);
/// this app treats anything else (including the key being absent, the common out-of-the-box state)
/// as "not configured," not a guessed on/off, since Windows itself decides the effective state from
/// driver capability in that case.</summary>
public sealed class GpuHagsInfo
{
    public int? HwSchModeRaw { get; init; }

    public string StateText => HwSchModeRaw switch
    {
        2 => "On",
        1 => "Off",
        null => "Not configured (default)",
        _ => $"Unknown ({HwSchModeRaw})",
    };

    public bool IsOn => HwSchModeRaw == 2;

    /// <summary>Best-effort proxy for "does the installed driver support HAGS at all" - HAGS needs
    /// a WDDM 2.7+ driver, and this app already reads a best-effort WDDM major.minor figure
    /// (GpuAdapterIdentity.WddmVersion) for the "Installed adapters" card. There's no direct API
    /// this app can reach that reports HAGS driver-capability as a plain bool, so this is a
    /// derived quick flag, not a verdict - see GpuViewModel's remarks.</summary>
    public bool? DriverLikelySupportsHags { get; init; }
}

/// <summary>#673: one driver-version "epoch" bucketing TDR/crash events by the driver version that
/// was installed at the time, from the driver packages currently staged in the driver store
/// (`pnputil /enum-drivers /class Display`) - see GpuRegistryService.ReadDisplayDriverVersionHistory
/// for exactly what "installed at the time" means here (a best-effort join on package publish date,
/// not a true point-in-time install log Windows doesn't keep).</summary>
public sealed class GpuDriverVersionBucket
{
    public string DriverVersion { get; init; } = string.Empty;
    public DateTime? PublishDate { get; init; }
    public int TdrCount { get; init; }
    public int CrashCount { get; init; }

    /// <summary>True for the most recent (highest publish date) package in the store - the
    /// currently-installed driver, as opposed to an older package Windows kept staged.</summary>
    public bool IsCurrent { get; init; }
}
