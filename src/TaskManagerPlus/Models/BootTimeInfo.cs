namespace TaskManagerPlus.Models;

/// <summary>One named boot-time component read from the most recent Diagnostics-Performance
/// event 100 (#89) - e.g. "MainPathBootTime", "BootPostBootTime". Field names/count aren't a
/// documented, versioned contract (see BootPerformanceService's remarks), so this is a plain
/// adaptive list rather than fixed named properties - whatever the event actually reported.</summary>
public sealed class BootTimeComponent
{
    public string Label { get; init; } = string.Empty;
    public int Milliseconds { get; init; }
}

/// <summary>Boot time breakdown for the most recent boot (#89).</summary>
public sealed class BootTimeBreakdown
{
    public DateTime BootTime { get; init; }
    public List<BootTimeComponent> Components { get; init; } = new();

    /// <summary>The largest named component found - in practice the one that represents the
    /// overall total (sub-phases are smaller slices of it), since this event's exact field
    /// naming isn't a documented contract this app can rely on matching by name alone.</summary>
    public int? TotalMs => Components.Count == 0 ? null : Components.Max(c => c.Milliseconds);

    // #703: the two named phases Microsoft's own boot-performance guidance talks about directly
    // ("main path" vs "post-boot") - pulled out of the adaptive Components list above by label
    // rather than parsed a second time, since ExtractBootTimeFields already normalized the names.
    public int? MainPathBootMs => Components.FirstOrDefault(c => c.Label.Contains("Main Path", StringComparison.OrdinalIgnoreCase))?.Milliseconds;
    public int? PostBootMs => Components.FirstOrDefault(c => c.Label.Contains("Post Boot", StringComparison.OrdinalIgnoreCase))?.Milliseconds;

    /// <summary>Whatever's left of TotalMs once the two named phases above are accounted for -
    /// shown as a third, unlabeled segment in the stacked bar rather than silently dropped.</summary>
    public int OtherMs => Math.Max(0, (TotalMs ?? 0) - (MainPathBootMs ?? 0) - (PostBootMs ?? 0));

    // #705: this boot's classification (full/hybrid-resume/hibernate-resume), read separately
    // from Microsoft-Windows-Kernel-Boot event 27 - null when that channel/event isn't available.
    public BootType? Type { get; init; }
}

/// <summary>Boot classification from Microsoft-Windows-Kernel-Boot event 27 (#705) - distinguishes
/// a real cold boot from a Fast Startup (hybrid) resume or a resume from hibernation, three cases
/// "boot time" means something very different for. Not a documented, versioned schema, so any
/// value outside 0-2 (or the event/channel being unavailable at all) degrades to null (Unknown)
/// rather than a guess - see BootPerformanceService.ReadLatestBootType.</summary>
public enum BootType
{
    Full = 0,
    FastStartupResume = 1,
    ResumeFromHibernate = 2,
}

/// <summary>Display-label helper shared by the ViewModel and XAML converters, so the three boot
/// type names are spelled the same way everywhere they appear.</summary>
public static class BootTypeExtensions
{
    public static string ToDisplayLabel(this BootType? type) => type switch
    {
        BootType.Full => "Full restart",
        BootType.FastStartupResume => "Fast Startup resume",
        BootType.ResumeFromHibernate => "Resume from hibernate",
        _ => "Unknown boot type",
    };
}

/// <summary>One persisted boot-time-trend sample (#90) - just enough to chart "is my boot time
/// getting worse over time" across sessions.</summary>
public sealed class BootHistoryEntry
{
    public DateTime Timestamp { get; init; }
    public int TotalMs { get; init; }

    // #705: null for any entry recorded before this field existed, or when Kernel-Boot event 27
    // wasn't available at record time - System.Text.Json leaves a missing JSON property as the
    // default (null, for a nullable property) on deserialize, so old boot-history.json entries
    // still load fine and just show "Unknown" boot type rather than failing the whole file.
    public BootType? Type { get; init; }
}

/// <summary>One degradation-family event read from the Diagnostics-Performance channel (#701) -
/// event 101 (an application took longer than usual to start), 102 (driver init delay), 103
/// (service start delay), 106 (background optimization), 109 (device init delay), or 110 (boot
/// degradation summary). Field names/count aren't a documented contract (same adaptive-read
/// tradeoff as BootTimeComponent above), so Name/TotalTimeMs/DegradationTimeMs are all nullable -
/// shown as "Unknown" pieces rather than fabricated when a particular event doesn't carry one.</summary>
public sealed class BootDegradationEvent
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int? TotalTimeMs { get; init; }
    public int? DegradationTimeMs { get; init; }

    /// <summary>Whichever of DegradationTime/TotalTime the event actually carried - the number
    /// that matters for "how much did this cost", falling back to whichever field is present.</summary>
    public int? ImpactMs => DegradationTimeMs ?? TotalTimeMs;
}

/// <summary>One ranked row on the "slow-boot culprit board" (#702) - a name (app/driver/service)
/// grouped and summed across every boot the Diagnostics-Performance channel still retains, so a
/// driver that costs a few seconds on every boot outranks a one-off long stall.</summary>
public sealed class BootCulprit
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public double TotalDegradationMs { get; init; }
    public int BootsAffected { get; init; }
    public int BootsObserved { get; init; }

    public string AppearedText => BootsObserved > 0
        ? $"Appeared in {BootsAffected} of the last {BootsObserved} boots"
        : string.Empty;
}

/// <summary>Firmware POST time (#704) from the ACPI Firmware Performance Data Table, read via
/// GetSystemFirmwareTable('ACPI','FPDT'). TableFound/PointerRecordFound distinguish "this
/// firmware doesn't publish the table at all" (common on older/OEM-locked firmware) from "the
/// table exists, but resolving its Boot Performance Pointer Record needs raw physical-memory
/// access, which Windows has blocked from user mode - even elevated processes - since Vista SP1,
/// with no documented replacement API." Both cases show "Unknown" in the UI, just with a
/// different explanation via UnavailableReason - see BootPerformanceService.ReadFirmwareBootTime.</summary>
public sealed class FirmwareBootTime
{
    public bool TableFound { get; init; }
    public bool PointerRecordFound { get; init; }
    public int? Milliseconds { get; init; }

    public string UnavailableReason => Milliseconds is not null ? string.Empty
        : !TableFound ? "This firmware doesn't publish boot-performance data."
        : !PointerRecordFound ? "Firmware boot-performance record not found."
        : "Not accessible to a Windows application (needs kernel-level physical-memory access).";
}

/// <summary>One boot-type bucket's median boot time (#706) - "Fast Startup resume: 9s · full
/// restart: 71s" is the single most common reason a user's perceived boot time swings wildly.</summary>
public sealed class BootTypeStat
{
    public BootType Type { get; init; }
    public int MedianMs { get; init; }
    public int Count { get; init; }

    public string Text => $"{((BootType?)Type).ToDisplayLabel()}: {MedianMs / 1000.0:0.#}s ({Count})";
}

/// <summary>"This boot was 2.4x your normal" quick flag (#707) - compares the most recent boot
/// against the rolling median of same-boot-type entries already in boot-history.json. A flag, not
/// a verdict: a boot that's genuinely slower for an unrelated one-off reason (e.g. a Windows
/// Update finishing up) looks identical to this heuristic as a newly-arrived, persistent
/// slowdown - see BootPerformanceService.ComputeRegressionFlag.</summary>
public sealed class BootRegressionFlag
{
    public double Ratio { get; init; }
    public string Message { get; init; } = string.Empty;
    public List<BootDegradationEvent> DegradationRows { get; init; } = new();
}
