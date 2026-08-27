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
}
