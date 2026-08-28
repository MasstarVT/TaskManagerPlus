using System.Collections.ObjectModel;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// #200: "filtered evidence bundle export" - composed onto EventsViewModel as a sub-ViewModel (the
/// same pattern Etw/Servicing already use for their own toggleable overlay panels), reached from
/// the Events tab (the diagnostic hub) via its own "Export evidence bundle" toggle. Channel
/// selection is deliberately NOT owned here - EventsViewModel already has a per-channel multi-select
/// checkbox on the channel tree (#112's "Multi-channel query" IsSelectedForMulti), so this reuses
/// that exact selection at export time via <see cref="ResolveChannels"/> (falling back to a sensible
/// default set when nothing is checked) rather than building a second channel picker.
/// </summary>
public sealed class EvidenceBundleViewModel : ObservableObject
{
    private readonly EvidenceBundleService _service = new();

    private bool _isPanelOpen;
    public bool IsPanelOpen { get => _isPanelOpen; set => SetProperty(ref _isPanelOpen, value); }

    /// <summary>The time-window picker - a lookback-days figure (matching this app's existing
    /// AnomalyLookbackDays/DiffCutoffDate style of time-window control) rather than a full calendar
    /// date-time pair, since every other on-demand scan in this app already expresses its window
    /// this way.</summary>
    private int _lookbackDays = 7;
    public int LookbackDays { get => _lookbackDays; set => SetProperty(ref _lookbackDays, value); }

    private bool _isExporting;
    // Note: ExportCommand is an AsyncRelayCommand, which has no RaiseCanExecuteChanged - its
    // CanExecute is re-evaluated automatically via CommandManager.InvalidateRequerySuggested,
    // called from AsyncRelayCommand.Execute itself before/after running.
    public bool IsExporting { get => _isExporting; private set => SetProperty(ref _isExporting, value); }

    private string? _statusText;
    public string? StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private string? _resultFolderPath;
    public string? ResultFolderPath { get => _resultFolderPath; private set => SetProperty(ref _resultFolderPath, value); }

    public ObservableCollection<EvidenceBundleStepResult> Steps { get; } = new();

    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand RevealFolderCommand { get; }

    /// <summary>Supplies the channel list at export time - EventsViewModel wires this to its own
    /// channel-tree selection plus any channels the last anomaly/timeline scan flagged, defaulting
    /// to System+Application when nothing else is available. Never called with no channels at all -
    /// see ExportAsync's own fallback if this delegate is unset.</summary>
    public Func<List<string>>? ResolveChannels { get; set; }

    public EvidenceBundleViewModel()
    {
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsExporting);
        RevealFolderCommand = new RelayCommand(_ => EtwTraceService.RevealInExplorer(ResultFolderPath ?? string.Empty), _ => ResultFolderPath is not null);
    }

    private async Task ExportAsync()
    {
        var channels = ResolveChannels?.Invoke() ?? new List<string>();
        if (channels.Count == 0) channels = new List<string> { "System", "Application" };

        var request = new EvidenceBundleRequest
        {
            Channels = channels,
            StartUtc = DateTime.UtcNow.AddDays(-Math.Max(1, LookbackDays)),
            EndUtc = DateTime.UtcNow,
        };

        IsExporting = true;
        Steps.Clear();
        ResultFolderPath = null;
        StatusText = "Starting export...";
        var progress = new Progress<string>(msg => StatusText = msg);
        try
        {
            var result = await _service.ExportAsync(request, progress, CancellationToken.None);
            foreach (var step in result.Steps) Steps.Add(step);
            ResultFolderPath = result.FolderPath;
            StatusText = result.AnyStepFailed
                ? $"Bundle exported to {result.FolderPath} - some steps were skipped (see the list below)."
                : $"Bundle exported to {result.FolderPath}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't export the evidence bundle: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }
}
