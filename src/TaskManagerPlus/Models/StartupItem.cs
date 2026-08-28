using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

public enum StartupSource
{
    RegistryRunHkcu,
    RegistryRunHklm,
    RegistryRunHklmWow6432,
    StartupFolderUser,
    StartupFolderAllUsers,
}

public sealed class StartupItem : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public StartupSource Source { get; init; }
    public string SourceDescription => Source switch
    {
        StartupSource.RegistryRunHkcu => "Registry (current user)",
        StartupSource.RegistryRunHklm => "Registry (all users)",
        StartupSource.RegistryRunHklmWow6432 => "Registry (all users, 32-bit)",
        StartupSource.StartupFolderUser => "Startup folder (current user)",
        StartupSource.StartupFolderAllUsers => "Startup folder (all users)",
        _ => "Unknown",
    };

    private bool _isEnabled;
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }

    // #91: measured (not estimated) delay between boot and this item's process actually starting,
    // for whichever items are still running - see StartupDelayService's remarks.
    private string _measuredDelayText = "Checking...";
    public string MeasuredDelayText { get => _measuredDelayText; set => SetProperty(ref _measuredDelayText, value); }

    /// <summary>Round 8 #22: combined startup-impact score ("Low impact"/"Medium impact"/
    /// "High impact"), blending the measured delay above with a quick CPU/memory footprint sample
    /// of the matched running process - see StartupDelayService.ScoreImpact.</summary>
    private string _impactText = "Checking...";
    public string ImpactText { get => _impactText; set => SetProperty(ref _impactText, value); }

    private string _impactDetailText = string.Empty;
    public string ImpactDetailText { get => _impactDetailText; set => SetProperty(ref _impactDetailText, value); }

    /// <summary>Round 8 #18: "Signed"/"Unsigned"/"Unknown" for the item's target executable -
    /// reuses SignatureCheckService (extracted from ProcessMonitorService's Round 2 per-file-path
    /// cache) rather than duplicating the check.</summary>
    private string _signatureStatus = "Checking...";
    public string SignatureStatus { get => _signatureStatus; set => SetProperty(ref _signatureStatus, value); }

    /// <summary>#837: signing certificate's subject CN (falling back to issuer CN, then
    /// "Unknown") - see SignatureCheckService.GetSignerInfo, populated alongside SignatureStatus
    /// above in StartupViewModel.Refresh.</summary>
    private string _publisher = "Checking...";
    public string Publisher { get => _publisher; set => SetProperty(ref _publisher, value); }

    private bool _isSelfSigned;
    public bool IsSelfSigned { get => _isSelfSigned; set => SetProperty(ref _isSelfSigned, value); }

    /// <summary>Round 8 #21: file size and last-modified time of the target executable, read by
    /// StartupManagerService.Sample() - null for a file that can't be resolved or no longer exists.</summary>
    public long? FileSizeBytes { get; init; }
    public DateTime? LastModifiedUtc { get; init; }
}
