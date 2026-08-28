using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>Profile load duration for one sign-in (#720) - Microsoft-Windows-User Profile
/// Service/Operational events 1 (load start) and 2 (load end) bracket the load; the timestamp
/// delta is the profile load time. Paired by the user SID/name field the events carry (read
/// adaptively - see ProfileDiagnosticsService.ReadProfileLoadTimings).</summary>
public sealed class ProfileLoadTiming
{
    public string UserKey { get; init; } = string.Empty; // whatever identifier (SID or account name) the events carried
    public DateTime LoadStart { get; init; }
    public DateTime LoadEnd { get; init; }
    public double DurationMs => Math.Max(0, (LoadEnd - LoadStart).TotalMilliseconds);
}

/// <summary>On-demand profile size/file-count walk (#720) - gated behind an explicit button
/// (never on a tick, per this app's on-demand-vs-polled convention), so an enormous AppData is
/// visible as a candidate reason for a slow profile load. TruncatedByBudget is set when the walk
/// hit its time/file-count safety cap rather than finishing - the reported numbers are then a
/// lower bound, not the true total, and the UI says so.</summary>
public sealed class ProfileSizeInfo
{
    public string ProfileImagePath { get; init; } = string.Empty;
    public long TotalBytes { get; init; }
    public long FileCount { get; init; }
    public bool TruncatedByBudget { get; init; }
    public string? Error { get; init; }

    public string SizeText => Error is not null ? "Unknown" : Formatting.FormatBytes(TotalBytes) + (TruncatedByBudget ? "+" : "");
    public string SummaryText => Error is not null
        ? $"Couldn't measure this profile: {Error}"
        : $"{SizeText} across {FileCount:N0} file(s){(TruncatedByBudget ? " (stopped early - size walk hit its safety limit; actual total is larger)" : "")}";
}

/// <summary>One SID subkey of HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList
/// (#721/#722) - Windows' own inventory of every profile it knows about on this machine.
/// State/RefCount are the documented values Windows itself uses to track a profile's load state;
/// a ".bak"-suffixed SID is a temporary-profile marker Windows creates itself when it can't load
/// the real one. Every flag here is a "quick flag, not a verdict" pattern-match on otherwise
/// ambiguous data (see CLAUDE.md's cross-cutting conventions) - a profile can legitimately be
/// mid-load (transient nonzero RefCount) when this is read.</summary>
public sealed class ProfileListEntry : ObservableObject
{
    public string Sid { get; init; } = string.Empty;
    public bool IsBakSuffixed { get; init; }
    public int? State { get; init; }
    public int? RefCount { get; init; }
    public string? ProfileImagePath { get; init; }
    public bool ProfileImagePathExists { get; init; }
    public string? CentralProfile { get; init; } // roaming profile UNC path, when configured

    // #720: on-demand size/file-count walk result for this specific row - null until "Measure
    // size" is clicked for it (see StartupViewModel.MeasureProfileSizeAsync). Mutable/observable
    // (unlike this class's other init-only fields) since it's populated well after the row is
    // first created and bound.
    private ProfileSizeInfo? _sizeInfo;
    public ProfileSizeInfo? SizeInfo { get => _sizeInfo; set => SetProperty(ref _sizeInfo, value); }

    private bool _isMeasuringSize;
    public bool IsMeasuringSize { get => _isMeasuringSize; set => SetProperty(ref _isMeasuringSize, value); }

    /// <summary>Bit 0x1 (PI_TEMPORARY-ish "not fully loaded") or 0x2000 ("temporary profile" flag)
    /// being set is the ProfileList schema's own signal that this SID has an outstanding issue -
    /// not a documented enum with named bits Microsoft publishes, so this only reads "nonzero" as
    /// worth surfacing rather than decoding individual bit meanings.</summary>
    public bool StateLooksUnhealthy => State is not null and not 0;

    public bool RefCountStuck => RefCount is > 0;

    public bool PathMissing => !string.IsNullOrEmpty(ProfileImagePath) && !ProfileImagePathExists;

    public bool IsRoaming => !string.IsNullOrWhiteSpace(CentralProfile);

    /// <summary>#721: the headline "you're signed into a temporary profile" case - a .bak SID
    /// pairing with a real one, a profile whose on-disk path no longer exists, or a state flag
    /// Windows itself marks as unhealthy.</summary>
    public bool LooksLikeTempOrCorrupt => IsBakSuffixed || PathMissing || StateLooksUnhealthy;

    public string FlagSummary
    {
        get
        {
            var flags = new List<string>();
            if (IsBakSuffixed) flags.Add(".bak SID (Windows created a backup profile key for this user)");
            if (PathMissing) flags.Add("ProfileImagePath no longer exists on disk");
            if (StateLooksUnhealthy) flags.Add($"State flag set (0x{State:X})");
            if (RefCountStuck) flags.Add($"RefCount stuck at {RefCount}");
            if (IsRoaming) flags.Add("roaming (CentralProfile configured)");
            return flags.Count == 0 ? "No issues found" : string.Join(" · ", flags);
        }
    }
}

/// <summary>One User Profile Service (Application log) diagnostic event correlated against the
/// ProfileList scan (#721/#722/#723) - 1500/1502/1511/1515 (temp/corrupt profile family), 1509/
/// 1521 (roaming copy/sync errors), 1530 (registry hive still in use by other processes). The
/// message text is read via FormatDescription() rather than re-parsed into named fields wherever
/// this app doesn't need to act on a specific piece of it (same "read the rendered message"
/// tradeoff PowerTimelineService/EventLogService already take) - LeakedProcessNames is the one
/// exception, extracted for #723's Processes-tab cross-link.</summary>
public sealed class ProfileServiceEventEntry
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>#723: process names parsed out of a 1530 "registry file is still in use" message
    /// (adaptive regex over the rendered text, same tradeoff as EventLogService.FaultingModuleRegex) -
    /// empty for every other event ID, or when 1530's message layout couldn't be matched.</summary>
    public List<string> LeakedProcessNames { get; init; } = new();

    public string CategoryText => EventId switch
    {
        1500 => "Profile service",
        1502 => "Temporary profile",
        1511 => "Profile not found",
        1515 => "Profile load issue",
        1509 => "Roaming copy error",
        1521 => "Roaming profile",
        1530 => "Registry handle leak",
        _ => "Profile event",
    };
}
