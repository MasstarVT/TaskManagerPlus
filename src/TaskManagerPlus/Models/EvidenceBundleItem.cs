using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>#981: which collector a <see cref="EvidenceBundleItem"/> row runs - one per artifact
/// #981 lists. EvidenceBundleService.CollectAsync switches on this to invoke the right collector,
/// rather than the catalog carrying a delegate directly (a plain enum keeps the catalog itself a
/// simple, inspectable data table - see EvidenceBundleService.BuildCatalog).</summary>
public enum EvidenceBundleCollectorKind
{
    MsInfo32,
    DxDiag,
    SystemInfo,
    DriverQuery,
    BatteryReport,
    SleepStudy,
    EnergyReport,
    PnpUtilDrivers,
    EventLogSystem,
    EventLogApplication,
    Minidumps,
    AppFindings,
    AppTimeline,
    AppBaselines,
}

/// <summary>
/// #983: one row in the evidence-bundle checklist dialog/panel - what the collector gathers, a
/// one-line "why this helps" description, a rough estimated size (labelled as such - #983 is
/// explicit this only needs to be directionally useful, not exact), whether the user has it
/// checked, and whether the file(s) it produces are plain text (so #984's scrubber has something
/// to run over - binary artifacts like minidumps/evtx are excluded, see EvidenceBundleService's
/// remarks).
/// </summary>
public sealed class EvidenceBundleItem : ObservableObject
{
    public required EvidenceBundleCollectorKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    /// <summary>Rough, labelled-as-a-guess byte estimate used only to build the running total on
    /// the checklist - real collected sizes go in the manifest (#986) after the fact.</summary>
    public required long EstimatedSizeBytes { get; init; }

    public string EstimatedSizeLabel => Formatting.FormatBytes(EstimatedSizeBytes);

    /// <summary>True when this collector's output is plain text and safe to run #984's scrubber
    /// over - false for minidumps and the .evtx event-log exports (binary formats; evtx in
    /// particular isn't practical to text-scrub safely, so it's always excluded - see #984's task
    /// notes and EvidenceBundleService's remarks).</summary>
    public required bool IsTextScrubbable { get; init; }

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value)) Changed?.Invoke(); }
    }

    /// <summary>Raised whenever IsSelected changes, so EvidenceBundleViewModel can recompute the
    /// running estimated-total-size text without every item needing its own PropertyChanged
    /// subscription wired up by hand.</summary>
    public event Action? Changed;
}
