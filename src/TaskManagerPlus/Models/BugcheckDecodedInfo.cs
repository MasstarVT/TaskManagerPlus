namespace TaskManagerPlus.Models;

/// <summary>Round 15, item 29: one decoded bugcheck parameter row - Label is always populated
/// ("Parameter N" for a code BugcheckParameterLookup doesn't have an entry for), Value is the
/// already-formatted hex string carried on the record, StatusText is item #30's NTSTATUS decode
/// when this parameter is documented as an exception/status code and the value matched a known
/// one (null otherwise). DisplayText is the single ready-to-bind string XAML actually shows -
/// computed once here rather than via a converter, since a Run element (unlike a TextBlock) can't
/// carry its own Visibility trigger to conditionally hide just the "(STATUS_NAME)" suffix.</summary>
public sealed class BugcheckParameterRow
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string? StatusText { get; init; }

    public string DisplayText => StatusText is null ? $"{Label}: {Value}" : $"{Label}: {Value} ({StatusText})";
}

/// <summary>
/// Round 15, items 28-37: everything decoded from a bugcheck code plus its (up to four) raw
/// parameters - the single result type both the event-log-driven Minidumps card
/// (EventLogService attaches one to each MinidumpInfo) and the binary-parse-driven Dump analysis
/// card (StabilityViewModel attaches one to each DumpRowViewModel) render from, so a given stop
/// code decodes identically regardless of which path found it. Built by BugcheckDecoder; item
/// #35's pool-tag resolution is the only potentially-slow part (a bounded driver-binary scan), so
/// every caller builds this off the UI thread (see EventLogService.Query and
/// StabilityViewModel.BuildDumpAnalysisBundle, both already backgrounded).
/// </summary>
public sealed class BugcheckDecodedInfo
{
    public List<BugcheckParameterRow> ParameterRows { get; init; } = new();

    /// <summary>#31: curated, non-authoritative guidance text - null when this code has none.</summary>
    public string? Guidance { get; init; }

    /// <summary>#32: DPC_WATCHDOG_VIOLATION (0x133) subtype line - null for every other code.</summary>
    public string? DpcWatchdogSubtypeText { get; init; }

    /// <summary>#33: DRIVER_POWER_STATE_FAILURE (0x9F) subcode line - null for every other code.
    /// The separate sleep/resume timestamp cross-reference lives on BugCheckRecord/MinidumpInfo
    /// instead (it needs the crash's own timestamp, which this parameter-only decode doesn't
    /// have).</summary>
    public string? DriverPowerStateSubcodeText { get; init; }

    /// <summary>#35: pool-tag decode for BAD_POOL_CALLER (0xC2) / DRIVER_CORRUPTED_EXPOOL (0xC5) -
    /// null when the code doesn't carry one or no plausible tag could be extracted at all.</summary>
    public string? PoolTagText { get; init; }

    /// <summary>Round 19, item 88: the bare 4-character tag PoolTagText above was built from (null
    /// whenever PoolTagText is null) - kept separate from the friendly PoolTagText display string
    /// so the "Apply Special Pool for this tag" button's CommandParameter has a plain tag to bind
    /// to rather than having to re-parse it back out of the display text.</summary>
    public string? PoolTagRaw { get; init; }

    /// <summary>#37: fault-address classification for 0xA/0xD1/0x50's referenced address - null
    /// for every other code.</summary>
    public string? FaultAddressClassification { get; init; }

    /// <summary>Round 19, item 85: Parameter 1's Verifier-specific subcode meaning for 0xC4/0xC9/
    /// 0xE6 (the three bugchecks Driver Verifier itself raises) - null for every other code, and
    /// null when Parameter 1 isn't a value this app's subcode table (VerifierViolationLookup)
    /// recognizes at all (a bare hex fallback still appears in the plain ParameterRows above in
    /// that case, never hidden).</summary>
    public string? VerifierViolationText { get; init; }
}
