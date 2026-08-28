using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>
/// #497: Driver Verifier's current state, combined from two independent sources - the CONFIGURED
/// state (Session Manager\Memory Management's VerifyDrivers/VerifyDriverLevel registry values,
/// readable without a reboot, but only takes effect after one) and the ACTIVE state (`verifier
/// /query`'s own report of what's actually running under Verifier since the last boot). The two can
/// disagree right after #498 enables Verifier and before the next restart - both are shown rather
/// than collapsed into one "is it on" bool, so that distinction isn't hidden.
/// </summary>
public sealed class DriverVerifierStatus
{
    /// <summary>True when VerifyDrivers/VerifyDriverLevel are non-empty/non-zero - Verifier is
    /// configured to run, whether or not it's actually active yet this boot.</summary>
    public bool IsConfigured { get; init; }

    /// <summary>True when VerifyDrivers is exactly "*" - every non-Microsoft driver (not literally
    /// every driver; Windows always excludes its own core drivers from a wildcard selection).</summary>
    public bool VerifiesAllDrivers { get; init; }

    public List<string> ConfiguredDriverNames { get; init; } = new();
    public uint VerifyLevelRaw { get; init; }

    /// <summary>Best-effort decode of VerifyLevelRaw's individual bits against Microsoft's
    /// documented Driver Verifier flag list - an unrecognized bit is never silently dropped, see
    /// DriverVerifierService.DescribeFlags.</summary>
    public List<string> EnabledChecks { get; init; } = new();

    /// <summary>True when `verifier /query` reports at least one driver currently being verified
    /// this boot - the authoritative "is it actually running right now" signal, vs. IsConfigured's
    /// "will it run after the next restart".</summary>
    public bool IsActiveThisBoot { get; init; }

    public List<string> ActiveDriverNames { get; init; } = new();

    /// <summary>Set only when `verifier /query` itself failed to run (rare - it's a normal signed
    /// Windows tool) - null the rest of the time, including when it ran fine and simply reported
    /// nothing active.</summary>
    public string? QueryError { get; init; }
}

/// <summary>#500 cross-reference support: one candidate driver offered for #498's setup wizard -
/// distinct .sys file names pulled from the driver inventory grid, third-party only, Microsoft
/// excluded from the list entirely rather than merely discouraged (the suggestion text calls for
/// Microsoft drivers to be "excluded/discouraged" - this app takes the stronger reading).</summary>
public sealed class VerifierCandidateDriver : ObservableObject
{
    public string FileName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? CompanyName { get; init; }

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
}
