using TaskManagerPlus.Common;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Round 19, item 83: one row in the guided Verifier wizard's driver-selection step - wraps the
/// immutable NonMicrosoftDriverCandidate with the mutable per-row IsSelected checkbox state a
/// plain init-only model can't carry itself, the same shape WerReportRowViewModel/DumpRowViewModel
/// already use elsewhere on this tab.
/// </summary>
public sealed class DriverVerifierCandidateRow : ObservableObject
{
    public NonMicrosoftDriverCandidate Candidate { get; }

    public string FileName => Candidate.FileName;
    public string ServiceName => Candidate.ServiceName;
    public string VendorText => string.IsNullOrWhiteSpace(Candidate.Vendor) ? "Unsigned / unknown vendor" : Candidate.Vendor!;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public DriverVerifierCandidateRow(NonMicrosoftDriverCandidate candidate)
    {
        Candidate = candidate;
    }
}
