namespace TaskManagerPlus.Models;

/// <summary>
/// Round 18, items 71-80: "will this PC actually capture the next BSOD" - reads
/// HKLM\SYSTEM\CurrentControlSet\Control\CrashControl (item 71) plus the page-file config that
/// determines whether the configured dump type can actually be written (items 72/73/74/75),
/// AutoReboot (76), Fast Startup/HiberbootEnabled (78), and hibernation/BitLocker interactions
/// (79) - see CrashDumpConfigService.ReadConfigurationAsync, which populates one instance of this
/// per refresh, consumed by both the detail card (71-79) and the summary checklist (80).
/// </summary>
public sealed record CrashDumpConfiguration
{
    // ---- Item 71: CrashControl read-out ----------------------------------------------------
    public int? CrashDumpEnabledRaw { get; init; }
    public string DumpTypeText { get; init; } = "Unknown";

    /// <summary>Whether the configured dump type actually needs a page file at all (anything but
    /// None/Unknown) - drives items 72/73's page-file-size check below.</summary>
    public bool DumpTypeNeedsPageFile { get; init; }

    public string? DumpFile { get; init; }
    public string? MinidumpDir { get; init; }
    public bool? Overwrite { get; init; }
    public bool? AutoReboot { get; init; }
    public bool? LogEvent { get; init; }
    public bool? AlwaysKeepMemoryDump { get; init; }
    public string? DedicatedDumpFile { get; init; }
    public int? DumpFileSizeMb { get; init; }
    public int? MinidumpsCount { get; init; }

    /// <summary>Plain-English label:value rows for item 71's "renders all of them in plain
    /// English" requirement - built once in CrashDumpConfigService rather than in the ViewModel,
    /// so both the card and (if ever needed) a support-bundle export read the exact same text.</summary>
    public List<CrashDumpConfigField> Fields { get; init; } = new();

    // ---- Items 72/73: dump type vs. page file validation ----------------------------------
    public long TotalRamBytes { get; init; }
    public List<PageFileConfigEntry> PageFiles { get; init; } = new();

    /// <summary>True when Windows has no page file configured at all (Win32_PageFileSetting
    /// returned zero rows) - item 73's "I disabled the page file because I have 64 GB of RAM"
    /// case, which silently disables crash dumps entirely.</summary>
    public bool PageFileDisabled { get; init; }

    /// <summary>Roughly RAM + overhead - the minimum a Complete memory dump needs. An
    /// approximation (Microsoft doesn't publish an exact formula); "worth a manual check," not a
    /// guarantee, per CLAUDE.md's "quick flag, not a verdict."</summary>
    public long RequiredSizeForCompleteBytes { get; init; }

    /// <summary>Rough minimum for a Kernel/Automatic/Active dump - smaller than a Complete dump
    /// (only kernel-mode pages), but Windows doesn't guarantee an exact figure either; same
    /// "approximation, not a verdict" caveat as above.</summary>
    public long RequiredSizeForKernelBytes { get; init; }

    /// <summary>Whichever of the two above actually applies to the currently configured dump
    /// type - 0 when the dump type doesn't need a page file at all (None/Unknown).</summary>
    public long RequiredSizeForConfiguredTypeBytes { get; init; }

    public bool PageFileOnSystemVolume { get; init; }

    /// <summary>True when the system volume's own page file (if any) is at least
    /// RequiredSizeForConfiguredTypeBytes - the "no dump at all can be written if the page file
    /// has been moved off C: [or is too small]" check from item 72.</summary>
    public bool SystemVolumePageFileSufficient { get; init; }

    // ---- Items 74/75: dedicated dump file + free space on the dump target ------------------
    /// <summary>DedicatedDumpFile when set, else DumpFile - whichever path the next crash would
    /// actually try to write to.</summary>
    public string? DumpTargetPath { get; init; }

    public string? DumpTargetVolume { get; init; }
    public long? DumpTargetFreeBytes { get; init; }
    public DumpTargetHealth DumpTargetHealthLevel { get; init; } = DumpTargetHealth.Unknown;
    public string DumpTargetHealthText { get; init; } = "Unknown - couldn't read free space on the dump target volume.";

    // ---- Item 78: Fast Startup ---------------------------------------------------------------
    public bool? HiberbootEnabled { get; init; }

    // ---- Item 79: hibernation + BitLocker interactions ---------------------------------------
    /// <summary>From `powercfg /a` - best-effort text, "Unknown" when powercfg's report can't be
    /// read/parsed at all (a different Windows build phrasing it differently, or the tool being
    /// blocked) rather than a guess.</summary>
    public string HibernationStatusText { get; init; } = "Unknown";

    /// <summary>From `manage-bde -status &lt;volume&gt;` for DumpTargetVolume - null when there's
    /// no dump target volume to check yet, "Unknown" when manage-bde itself couldn't be read
    /// (missing on this SKU, access denied, ...).</summary>
    public string? DumpVolumeBitLockerStatusText { get; init; }
}

/// <summary>Item 71: one plain-English label:value row for the "Crash dump configuration" card.</summary>
public sealed record CrashDumpConfigField(string Label, string Value);

public enum DumpTargetHealth { Unknown, Green, Amber, Red }

/// <summary>Items 72/73: one Win32_PageFileSetting entry, joined to the matching
/// Win32_PageFileUsage row (allocated size) when one exists.</summary>
public sealed record PageFileConfigEntry
{
    public string? Path { get; init; }
    public string? Volume { get; init; }
    public bool IsSystemVolume { get; init; }

    /// <summary>InitialSize/MaximumSize of 0/0 is Windows' own convention for "system managed" -
    /// there is no fixed configured size to compare against the required-size figures above.</summary>
    public bool IsSystemManaged { get; init; }

    public long InitialSizeMb { get; init; }
    public long MaximumSizeMb { get; init; }
    public long? AllocatedSizeMb { get; init; }

    /// <summary>Plain-English one-liner for the page-file list on the crash dump configuration
    /// card - computed here (not via an XAML converter) so it stays in one place regardless of
    /// how many places end up displaying a page file entry.</summary>
    public string DisplayText
    {
        get
        {
            string sizeText = IsSystemManaged
                ? $"system-managed{(AllocatedSizeMb is { } a and > 0 ? $" ({a:N0} MB currently allocated)" : string.Empty)}"
                : $"{InitialSizeMb:N0}-{MaximumSizeMb:N0} MB fixed range";
            string volumeText = IsSystemVolume ? $"{Volume} (system volume)" : Volume ?? "Unknown volume";
            return $"{Path} — {volumeText}, {sizeText}";
        }
    }
}

/// <summary>Item 80: one row of the "will this PC capture the next BSOD" checklist.</summary>
public sealed record CrashCaptureChecklistItem
{
    public string Label { get; init; } = string.Empty;

    /// <summary>true = pass, false = fail, null = couldn't be verified either way.</summary>
    public bool? Passed { get; init; }

    public string Detail { get; init; } = string.Empty;

    /// <summary>Whether this row is actually counted toward the overall verdict below, vs. shown
    /// purely as context (items 76/78/79 don't gate whether a dump gets written at all - they
    /// affect whether the stop code is visible and whether "did rebooting fix it" reasoning still
    /// holds - so they're informational rows, not capture gates).</summary>
    public bool AffectsCapture { get; init; } = true;
}

public enum CrashCaptureVerdict { Pass, Fail, Uncertain }

/// <summary>Item 80: the headline card - one pass/fail/uncertain verdict plus the checklist rows
/// that produced it.</summary>
public sealed record CrashCaptureChecklist
{
    public List<CrashCaptureChecklistItem> Items { get; init; } = new();
    public CrashCaptureVerdict Verdict { get; init; } = CrashCaptureVerdict.Uncertain;
    public string VerdictText { get; init; } = string.Empty;
}
