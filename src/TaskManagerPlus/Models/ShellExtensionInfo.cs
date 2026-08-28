using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>One registered Explorer shell extension (context-menu handler / icon-overlay COM
/// add-in / property sheet / copy hook / drag-drop / column handler, #20/#829) - a classic,
/// often-overlooked cause of a slow Explorer right-click menu. See Services/ShellExtensionService.
///
/// #829: now an ObservableObject rather than plain init-only data, since IsApproved can be
/// toggled in place from the Startup tab (ShellExtensionService.SetApproved) without a full
/// re-scan - the same "mutable row, toggled command flips one property" shape ScheduledTaskRow
/// already uses for its own IsEnabled.</summary>
public sealed class ShellExtensionInfo : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Clsid { get; init; } = string.Empty;
    public string DllPath { get; init; } = string.Empty;

    /// <summary>Certificate subject CN, when cheaply available. "Unknown" is expected and normal
    /// here - see AutorunEntry's remarks on why publisher extraction is out of scope for now.</summary>
    public string Publisher { get; init; } = "Unknown";

    /// <summary>"Signed" / "Unsigned" / "Unknown" for DllPath, via SignatureCheckService's shared
    /// per-path cache - a quick flag, not a verdict, same as everywhere else this app checks
    /// Authenticode signatures.</summary>
    public string SignatureStatus { get; init; } = "Unknown";

    private bool _isApproved;

    /// <summary>True when this CLSID appears in the "Shell Extensions\Approved" list - Windows
    /// itself uses this list to allow a handler to load without a warning prompt; an unapproved
    /// entry isn't necessarily broken, just informational. Mutable (#829): toggling this from the
    /// Startup tab writes/removes the CLSID under that same registry key via
    /// ShellExtensionService.SetApproved, then flips this property in place so the grid reflects
    /// the change immediately rather than requiring a full "Load shell extensions" re-scan.</summary>
    public bool IsApproved { get => _isApproved; set { if (SetProperty(ref _isApproved, value)) OnPropertyChanged(nameof(ApprovedText)); } }

    public string ApprovedText => IsApproved ? "Approved" : "Not listed";
}
