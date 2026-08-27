using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>
/// One Windows Scheduled Task (#79) - a huge, often-overlooked source of background slowdowns
/// and unwanted auto-launches the Startup tab's registry-Run/Startup-folder scan doesn't cover at
/// all. See Services/ScheduledTaskService for where each field comes from.
/// </summary>
public sealed class ScheduledTaskRow : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string NextRunTime { get; init; } = string.Empty;
    public string LastRunTime { get; init; } = string.Empty;
    public string LastResult { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string TaskToRun { get; init; } = string.Empty;

    private bool _isEnabled = true;
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }

    /// <summary>Logon-trigger delay text (#80, e.g. "Delay: 30s"), populated on demand via
    /// CheckDelayCommand - see ScheduledTaskService.ReadLogonDelay for why this isn't fetched for
    /// every row up front.</summary>
    private string _delayText = string.Empty;
    public string DelayText { get => _delayText; set => SetProperty(ref _delayText, value); }
}
