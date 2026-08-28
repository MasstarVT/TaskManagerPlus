using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Data;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs #175-183's "Servicing logs" panel of the Events tab - a toggleable overlay reached from
/// the Events tab, the same composition pattern #146-153's ETW trace capture panel already
/// established (see EtwCaptureViewModel's own remarks): a sub-ViewModel composed into
/// EventsViewModel as <c>Servicing</c> rather than folded onto EventsViewModel's own already-large
/// surface, and disposed alongside it.
///
/// Every read here is button-gated (no DispatcherTimer) except the two genuinely cheap ones
/// (#181's registry-only pending-servicing check and #183's one-folder size/count tally), which
/// load automatically the first time the panel opens - the exact same "cheap catalogs load
/// automatically, heavier reads stay behind their own button" split EtwCaptureViewModel's own
/// IsPanelOpen setter already uses.
/// </summary>
public sealed class ServicingLogsViewModel : ObservableObject, IDisposable
{
    private readonly StatusCodeResolverService _statusCodes = new();
    private CancellationTokenSource? _wuLogCts;

    private bool _isPanelOpen;
    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        set
        {
            if (!SetProperty(ref _isPanelOpen, value) || !value) return;
            if (PendingStatus is null && RefreshPendingStatusCommand.CanExecute(null)) RefreshPendingStatusCommand.Execute(null);
            if (LogHealth is null && RefreshLogHealthCommand.CanExecute(null)) RefreshLogHealthCommand.Execute(null);
        }
    }

    // ==================== #175: CBS.log parser ====================

    private CbsLogSummary? _cbsSummary;
    public CbsLogSummary? CbsSummary { get => _cbsSummary; private set => SetProperty(ref _cbsSummary, value); }

    private bool _isCbsLoading;
    public bool IsCbsLoading { get => _isCbsLoading; private set => SetProperty(ref _isCbsLoading, value); }

    public ObservableCollection<ResolvedCode> CbsDecodedCodes { get; } = new();
    private bool _isDecodingCbsCodes;
    public bool IsDecodingCbsCodes { get => _isDecodingCbsCodes; private set => SetProperty(ref _isDecodingCbsCodes, value); }

    public AsyncRelayCommand RefreshCbsLogCommand { get; }
    public AsyncRelayCommand DecodeCbsCodesCommand { get; }

    private async Task RefreshCbsLogAsync()
    {
        IsCbsLoading = true;
        CbsDecodedCodes.Clear();
        try
        {
            CbsSummary = await ServicingLogService.ParseCbsLogAsync();
        }
        finally
        {
            IsCbsLoading = false;
        }
    }

    private Task DecodeCbsCodesAsync() => DecodeCodesAsync(CbsSummary?.ErrorCodes, CbsDecodedCodes, v => IsDecodingCbsCodes = v);

    // ==================== #176: SFC result summary (CBS [SR] block, + CbsPersist archives) ====================

    private SfcResultSummary? _sfcSummary;
    public SfcResultSummary? SfcSummary { get => _sfcSummary; private set => SetProperty(ref _sfcSummary, value); }

    private bool _isSfcLoading;
    public bool IsSfcLoading { get => _isSfcLoading; private set => SetProperty(ref _isSfcLoading, value); }

    private string? _sfcStatusText;
    public string? SfcStatusText { get => _sfcStatusText; private set => SetProperty(ref _sfcStatusText, value); }

    public AsyncRelayCommand SummarizeSfcCommand { get; }

    private async Task SummarizeSfcAsync()
    {
        IsSfcLoading = true;
        SfcStatusText = "Scanning CBS.log for System File Checker activity...";
        try
        {
            var summary = await ServicingLogService.SummarizeSfcResultAsync(p => SfcStatusText = p);
            SfcSummary = summary;
            SfcStatusText = summary.Found
                ? $"Summarized from {string.Join(", ", summary.SourceLogs.Select(Path.GetFileName))}."
                : summary.ErrorMessage;
        }
        catch (Exception ex)
        {
            SfcStatusText = $"Couldn't summarize the SFC result: {ex.Message}";
        }
        finally
        {
            IsSfcLoading = false;
        }
    }

    // ==================== #177: DISM.log parser ====================

    private DismLogSummary? _dismSummary;
    public DismLogSummary? DismSummary { get => _dismSummary; private set => SetProperty(ref _dismSummary, value); }

    public ObservableCollection<DismLogEntry> DismEntries { get; } = new();

    private bool _isDismLoading;
    public bool IsDismLoading { get => _isDismLoading; private set => SetProperty(ref _isDismLoading, value); }

    public AsyncRelayCommand ParseDismLogCommand { get; }

    private async Task ParseDismLogAsync()
    {
        IsDismLoading = true;
        DismEntries.Clear();
        try
        {
            var summary = await ServicingLogService.ParseDismLogAsync();
            DismSummary = summary;
            foreach (var entry in summary.Entries) DismEntries.Add(entry);

            // #177: decode each entry's HRESULT inline via #124's StatusCodeResolverService -
            // bounded concurrency, the same shape EtwCaptureViewModel.RefreshSessionsAsync already
            // uses for its own "load detail for every row" follow-up pass.
            var withCodes = summary.Entries.Where(e => e.HResultCode is not null).ToList();
            if (withCodes.Count > 0)
            {
                using var gate = new SemaphoreSlim(4);
                var tasks = withCodes.Select(async entry =>
                {
                    await gate.WaitAsync();
                    try { entry.HResultMeaning = await _statusCodes.ResolveAsync(entry.HResultCode!); }
                    finally { gate.Release(); }
                });
                await Task.WhenAll(tasks);
                // DismLogEntry is a plain POCO (no INotifyPropertyChanged), same as EtwSessionRow -
                // force the bound grid to re-pull every cell now that HResultMeaning was filled in.
                CollectionViewSource.GetDefaultView(DismEntries).Refresh();
            }
        }
        finally
        {
            IsDismLoading = false;
        }
    }

    // ==================== #178: upgrade/setup failure analysis ====================

    private SetupFailureAnalysis? _setupAnalysis;
    public SetupFailureAnalysis? SetupAnalysis { get => _setupAnalysis; private set => SetProperty(ref _setupAnalysis, value); }

    private bool _isSetupAnalysisLoading;
    public bool IsSetupAnalysisLoading { get => _isSetupAnalysisLoading; private set => SetProperty(ref _isSetupAnalysisLoading, value); }

    public AsyncRelayCommand AnalyzeSetupFailureCommand { get; }

    private async Task AnalyzeSetupFailureAsync()
    {
        IsSetupAnalysisLoading = true;
        try
        {
            SetupAnalysis = await ServicingLogService.AnalyzeSetupFailureAsync();
        }
        finally
        {
            IsSetupAnalysisLoading = false;
        }
    }

    // ==================== #179: WindowsUpdate.log on demand (Get-WindowsUpdateLog) ====================

    private WindowsUpdateLogResult? _wuLogResult;
    public WindowsUpdateLogResult? WuLogResult { get => _wuLogResult; private set => SetProperty(ref _wuLogResult, value); }

    private bool _isWuLogRunning;
    public bool IsWuLogRunning
    {
        get => _isWuLogRunning;
        private set { if (SetProperty(ref _isWuLogRunning, value)) CancelWuLogCommand.RaiseCanExecuteChanged(); }
    }

    private string? _wuLogStatusText;
    public string? WuLogStatusText { get => _wuLogStatusText; private set => SetProperty(ref _wuLogStatusText, value); }

    public AsyncRelayCommand RunGetWindowsUpdateLogCommand { get; }
    public RelayCommand CancelWuLogCommand { get; }
    public RelayCommand OpenWuLogFileCommand { get; }

    /// <summary>#179: explicitly gated and progress-reported, since Get-WindowsUpdateLog decodes
    /// the WindowsUpdate ETL traces via tracerpt under the hood and can take tens of seconds to a
    /// couple of minutes - runs via Task.Run inside ServicingLogService itself (never on the UI
    /// thread), with IsWuLogRunning driving a busy indicator the whole time.</summary>
    private async Task RunGetWindowsUpdateLogAsync()
    {
        _wuLogCts?.Cancel();
        _wuLogCts?.Dispose();
        _wuLogCts = new CancellationTokenSource();
        var token = _wuLogCts.Token;

        IsWuLogRunning = true;
        WuLogStatusText = "Running Get-WindowsUpdateLog - this decodes the WindowsUpdate ETL traces via tracerpt and can take tens of seconds to a couple of minutes...";
        try
        {
            var result = await ServicingLogService.RunGetWindowsUpdateLogAsync(token);
            WuLogResult = result;
            WuLogStatusText = result.Success
                ? $"Done in {result.Duration:mm\\:ss} - {result.Failures.Count} failure line(s) found. Full decoded log: {result.LogFilePath}"
                : result.ErrorMessage;
        }
        catch (OperationCanceledException)
        {
            WuLogStatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            WuLogStatusText = $"Couldn't run Get-WindowsUpdateLog: {ex.Message}";
        }
        finally
        {
            IsWuLogRunning = false;
        }
    }

    private void OpenWuLogFile()
    {
        string? path = WuLogResult?.LogFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            WuLogStatusText = $"Couldn't open the log: {ex.Message}";
        }
    }

    // ==================== #180: combined update failure history ====================

    public ObservableCollection<UpdateHistoryEntry> UpdateHistory { get; } = new();

    private bool _isUpdateHistoryLoading;
    public bool IsUpdateHistoryLoading { get => _isUpdateHistoryLoading; private set => SetProperty(ref _isUpdateHistoryLoading, value); }

    private string? _updateHistoryStatusText;
    public string? UpdateHistoryStatusText { get => _updateHistoryStatusText; private set => SetProperty(ref _updateHistoryStatusText, value); }

    public AsyncRelayCommand LoadUpdateHistoryCommand { get; }

    private async Task LoadUpdateHistoryAsync()
    {
        IsUpdateHistoryLoading = true;
        UpdateHistoryStatusText = "Reading Windows Update Client, Setup, and QFE inventory history...";
        try
        {
            var list = await ServicingLogService.LoadUpdateHistoryAsync();
            UpdateHistory.Clear();
            foreach (var e in list) UpdateHistory.Add(e);
            int failures = list.Count(e => e.Success == false);
            UpdateHistoryStatusText = $"{list.Count} entr{(list.Count == 1 ? "y" : "ies")} loaded - {failures} failure(s).";
        }
        catch (Exception ex)
        {
            UpdateHistoryStatusText = $"Couldn't load update history: {ex.Message}";
        }
        finally
        {
            IsUpdateHistoryLoading = false;
        }
    }

    // ==================== #181: stuck-servicing detector ====================

    private PendingServicingStatus? _pendingStatus;
    public PendingServicingStatus? PendingStatus { get => _pendingStatus; private set => SetProperty(ref _pendingStatus, value); }

    private bool _isPendingCheckLoading;
    public bool IsPendingCheckLoading { get => _isPendingCheckLoading; private set => SetProperty(ref _isPendingCheckLoading, value); }

    public AsyncRelayCommand RefreshPendingStatusCommand { get; }

    private async Task RefreshPendingStatusAsync()
    {
        IsPendingCheckLoading = true;
        try
        {
            PendingStatus = await Task.Run(ServicingLogService.CheckPendingServicing);
        }
        finally
        {
            IsPendingCheckLoading = false;
        }
    }

    // ==================== #182: App/Store install failure channel ====================

    public ObservableCollection<EventRecordRow> AppxFailures { get; } = new();

    private bool _isAppxLoading;
    public bool IsAppxLoading { get => _isAppxLoading; private set => SetProperty(ref _isAppxLoading, value); }

    private string? _appxStatusText;
    public string? AppxStatusText { get => _appxStatusText; private set => SetProperty(ref _appxStatusText, value); }

    public AsyncRelayCommand LoadAppxFailuresCommand { get; }

    private async Task LoadAppxFailuresAsync()
    {
        IsAppxLoading = true;
        try
        {
            var list = await ServicingLogService.LoadAppxFailuresAsync();
            AppxFailures.Clear();
            foreach (var e in list) AppxFailures.Add(e);
            AppxStatusText = list.Count == 0
                ? "No Error/Warning events found in the AppX deployment or AppReadiness operational channels."
                : $"{list.Count} event(s) found.";
        }
        catch (Exception ex)
        {
            AppxStatusText = $"Couldn't read the AppX/AppReadiness channels: {ex.Message}";
        }
        finally
        {
            IsAppxLoading = false;
        }
    }

    // ==================== #183: servicing log health ====================

    private CbsLogHealth? _logHealth;
    public CbsLogHealth? LogHealth { get => _logHealth; private set { if (SetProperty(ref _logHealth, value)) RevealCbsFolderCommand.RaiseCanExecuteChanged(); } }

    private bool _isLogHealthLoading;
    public bool IsLogHealthLoading { get => _isLogHealthLoading; private set => SetProperty(ref _isLogHealthLoading, value); }

    public AsyncRelayCommand RefreshLogHealthCommand { get; }
    public RelayCommand RevealCbsFolderCommand { get; }

    private async Task RefreshLogHealthAsync()
    {
        IsLogHealthLoading = true;
        try
        {
            LogHealth = await Task.Run(ServicingLogService.GetCbsLogHealth);
        }
        finally
        {
            IsLogHealthLoading = false;
        }
    }

    // ==================== shared: on-demand code decoding via #124's StatusCodeResolverService ====================

    private async Task DecodeCodesAsync(List<string>? codes, ObservableCollection<ResolvedCode> target, Action<bool> setBusy)
    {
        if (codes is null || codes.Count == 0) return;
        setBusy(true);
        target.Clear();
        try
        {
            using var gate = new SemaphoreSlim(4);
            var rows = codes.Select(c => new ResolvedCode { Code = c }).ToList();
            foreach (var row in rows) target.Add(row);

            var tasks = rows.Select(async row =>
            {
                await gate.WaitAsync();
                try { row.Meaning = await _statusCodes.ResolveAsync(row.Code); }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
            CollectionViewSource.GetDefaultView(target).Refresh();
        }
        finally
        {
            setBusy(false);
        }
    }

    public ServicingLogsViewModel()
    {
        RefreshCbsLogCommand = new AsyncRelayCommand(RefreshCbsLogAsync, () => !IsCbsLoading);
        DecodeCbsCodesCommand = new AsyncRelayCommand(DecodeCbsCodesAsync, () => !IsDecodingCbsCodes && CbsSummary?.ErrorCodes.Count > 0);

        SummarizeSfcCommand = new AsyncRelayCommand(SummarizeSfcAsync, () => !IsSfcLoading);

        ParseDismLogCommand = new AsyncRelayCommand(ParseDismLogAsync, () => !IsDismLoading);

        AnalyzeSetupFailureCommand = new AsyncRelayCommand(AnalyzeSetupFailureAsync, () => !IsSetupAnalysisLoading);

        RunGetWindowsUpdateLogCommand = new AsyncRelayCommand(RunGetWindowsUpdateLogAsync, () => !IsWuLogRunning);
        CancelWuLogCommand = new RelayCommand(_ => _wuLogCts?.Cancel(), _ => IsWuLogRunning);
        OpenWuLogFileCommand = new RelayCommand(_ => OpenWuLogFile(), _ => WuLogResult?.LogFilePath is not null);

        LoadUpdateHistoryCommand = new AsyncRelayCommand(LoadUpdateHistoryAsync, () => !IsUpdateHistoryLoading);

        RefreshPendingStatusCommand = new AsyncRelayCommand(RefreshPendingStatusAsync, () => !IsPendingCheckLoading);

        LoadAppxFailuresCommand = new AsyncRelayCommand(LoadAppxFailuresAsync, () => !IsAppxLoading);

        RefreshLogHealthCommand = new AsyncRelayCommand(RefreshLogHealthAsync, () => !IsLogHealthLoading);
        RevealCbsFolderCommand = new RelayCommand(_ =>
        {
            if (LogHealth?.Exists == true) EtwTraceService.RevealInExplorer(LogHealth.FolderPath);
        }, _ => LogHealth?.Exists == true);
    }

    public void Dispose()
    {
        _wuLogCts?.Cancel();
        _wuLogCts?.Dispose();
    }
}
