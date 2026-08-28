using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

public sealed class ServicesViewModel : ObservableObject, IDisposable
{
    private readonly ServiceControlService _service = new();
    private readonly EventLogService _eventLog = new();
    private readonly DispatcherTimer _timer;
    private bool _isRefreshing;
    private bool _isBusy;

    public ObservableCollection<ServiceRow> Services { get; } = new();
    public ICollectionView ServicesView { get; }

    /// <summary>Round 7 #15: kernel/file-system driver "services" - a separate collection/view
    /// rather than mixed into Services, since drivers carry a much narrower set of meaningful
    /// columns (no dependencies, rarely a logon account) - see ServiceControlService.SampleDrivers.
    /// Only sampled while ShowDrivers is on, so a user who never opens this sub-view never pays
    /// for the extra enumeration.</summary>
    public ObservableCollection<ServiceRow> Drivers { get; } = new();

    private bool _showDrivers;
    public bool ShowDrivers
    {
        get => _showDrivers;
        set
        {
            if (SetProperty(ref _showDrivers, value) && value)
                _ = RefreshDriversAsync();
        }
    }

    private ServiceRow? _selectedService;
    public ServiceRow? SelectedService
    {
        get => _selectedService;
        set
        {
            var previous = _selectedService;
            if (SetProperty(ref _selectedService, value) && previous is not null)
                previous.FailureActionsText = string.Empty; // stale text from a previous selection would be misleading
        }
    }

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
                ServicesView.Refresh();
        }
    }

    private bool _failedToStartOnly;
    public bool FailedToStartOnly
    {
        get => _failedToStartOnly;
        set
        {
            if (SetProperty(ref _failedToStartOnly, value))
                ServicesView.Refresh();
        }
    }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RestartCommand { get; }
    public RelayCommand RefreshNowCommand { get; }
    public RelayCommand ViewFailureActionsCommand { get; }

    /// <summary>Round 7 #13: mines Service Control Manager 7036 events for approximate per-service
    /// start durations - a single on-demand pass merged onto whatever rows are already loaded,
    /// the same on-demand shape StabilityViewModel already uses for its own event-log query.</summary>
    public RelayCommand LoadStartDurationsCommand { get; }

    /// <summary>Round 7 #16: capture the current StartType/logon-account config as a baseline, or
    /// compare against a previously saved one - reuses SnapshotService/SystemSnapshot from Round 6
    /// rather than a second baseline file format.</summary>
    public RelayCommand CaptureConfigBaselineCommand { get; }
    public RelayCommand CheckConfigDriftCommand { get; }

    private string? _startDurationStatus;
    public string? StartDurationStatus { get => _startDurationStatus; set => SetProperty(ref _startDurationStatus, value); }

    public ServicesViewModel()
    {
        ServicesView = CollectionViewSource.GetDefaultView(Services);
        ServicesView.Filter = FilterPredicate;

        StartCommand = new RelayCommand(_ => _ = RunAction(ServiceControlService.Start, "started"), _ => CanStart());
        StopCommand = new RelayCommand(_ => _ = RunAction(ServiceControlService.Stop, "stopped"), _ => CanStop());
        RestartCommand = new RelayCommand(_ => _ = RunAction(ServiceControlService.Restart, "restarted"), _ => CanStop());
        RefreshNowCommand = new RelayCommand(_ => _ = RefreshAsync());
        ViewFailureActionsCommand = new RelayCommand(_ => _ = LoadFailureActionsAsync(), _ => SelectedService is not null);
        LoadStartDurationsCommand = new RelayCommand(_ => _ = LoadStartDurationsAsync());
        CaptureConfigBaselineCommand = new RelayCommand(_ => CaptureConfigBaseline());
        CheckConfigDriftCommand = new RelayCommand(_ => CheckConfigDrift());

        // Round 12, #100: configurable poll interval - see PollIntervalSettingsService's remarks.
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(PollIntervalSettingsService.Load().ServicesSeconds),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    /// <summary>Round 12, #100: how often the Services tab refreshes - default unchanged (2s).
    /// Loaded fresh from disk on every change (never cached) so this tab's slider can't clobber
    /// another tab's own saved interval in the same shared JSON file.</summary>
    public double PollIntervalSeconds
    {
        get => _timer.Interval.TotalSeconds;
        set
        {
            double clamped = Math.Clamp(value, 0.5, 10.0);
            if (Math.Abs(_timer.Interval.TotalSeconds - clamped) < 0.01) return;

            _timer.Interval = TimeSpan.FromSeconds(clamped);
            var settings = PollIntervalSettingsService.Load();
            settings.ServicesSeconds = clamped;
            PollIntervalSettingsService.Save(settings);
            OnPropertyChanged();
        }
    }

    // #980: read-only mode disables Start/Stop/Restart (mutating) but leaves everything else on
    // this tab (Refresh, failure-actions view, start-durations, config baseline/drift) working.
    private bool CanStart() => !IsBusy && !ReadOnlyModeService.IsReadOnly && SelectedService is { CanStart: true };
    private bool CanStop() => !IsBusy && !ReadOnlyModeService.IsReadOnly && SelectedService is { CanStop: true };

    private bool FilterPredicate(object obj)
    {
        if (obj is not ServiceRow row) return false;

        if (FailedToStartOnly && !row.HasFailedToStart) return false;

        if (string.IsNullOrWhiteSpace(FilterText)) return true;
        return row.DisplayName.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
               row.ServiceName.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var latest = await Task.Run(() => _service.Sample());
            MergeInto(latest);

            if (ShowDrivers)
                await RefreshDriversAsync();
        }
        catch
        {
            // Best-effort.
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void MergeInto(List<ServiceRow> latest)
    {
        var latestByName = latest.ToDictionary(r => r.ServiceName, StringComparer.OrdinalIgnoreCase);

        for (int i = Services.Count - 1; i >= 0; i--)
        {
            var existing = Services[i];
            if (latestByName.TryGetValue(existing.ServiceName, out var fresh))
            {
                existing.Status = fresh.Status;
                existing.StartType = fresh.StartType;
                existing.ProcessId = fresh.ProcessId;
                existing.ExitCode = fresh.ExitCode;
                existing.DependsOn = fresh.DependsOn;
                existing.DependentServices = fresh.DependentServices;
                existing.LogOnAs = fresh.LogOnAs;
                latestByName.Remove(existing.ServiceName);
            }
            else
            {
                Services.Remove(existing);
            }
        }

        foreach (var row in latestByName.Values)
            Services.Add(row);
    }

    private async Task RunAction(Func<string, (bool Success, string? Error)> action, string verbPast)
    {
        var target = SelectedService;
        if (target is null) return;

        string before = target.Status.ToString();
        IsBusy = true;
        try
        {
            var (success, error) = await Task.Run(() => action(target.ServiceName));
            StatusMessage = success
                ? $"{target.DisplayName} {verbPast}."
                : $"Couldn't control {target.DisplayName}: {error}";

            await RefreshAsync();

            // #972: record every mutation this app performs - after the refresh above so
            // AfterValue reflects the service's actual post-action status, not a stale
            // pre-refresh read (MergeInto updates `target` in place, so it's still the right row).
            ChangeJournalService.Append(new ChangeJournalEntry
            {
                Kind = ChangeKind.ServiceStateChange,
                Target = target.DisplayName,
                ActionDescription = $"Service {verbPast}",
                BeforeValue = before,
                AfterValue = target.Status.ToString(),
                TriggeredBy = "Services tab",
                Success = success,
                IsUndoable = success,
                ServiceName = target.ServiceName,
            });
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Loads SelectedService's recovery-actions text (#71) on demand - see
    /// ServiceControlService.ReadFailureActionsTextAsync for why this shells to sc.exe rather than
    /// every tick.</summary>
    private async Task LoadFailureActionsAsync()
    {
        var target = SelectedService;
        if (target is null) return;

        target.FailureActionsText = await ServiceControlService.ReadFailureActionsTextAsync(target.ServiceName);
    }

    /// <summary>Round 7 #15: (re)samples the driver sub-view - only ever called while ShowDrivers
    /// is on (the toggle setter, and the main RefreshAsync tick once it's already on).</summary>
    private async Task RefreshDriversAsync()
    {
        try
        {
            var latest = await Task.Run(() => _service.SampleDrivers());
            var latestByName = latest.ToDictionary(r => r.ServiceName, StringComparer.OrdinalIgnoreCase);

            for (int i = Drivers.Count - 1; i >= 0; i--)
            {
                var existing = Drivers[i];
                if (latestByName.TryGetValue(existing.ServiceName, out var fresh))
                {
                    existing.Status = fresh.Status;
                    existing.StartType = fresh.StartType;
                    latestByName.Remove(existing.ServiceName);
                }
                else
                {
                    Drivers.Remove(existing);
                }
            }
            foreach (var row in latestByName.Values)
                Drivers.Add(row);
        }
        catch
        {
            // Best-effort.
        }
    }

    /// <summary>Round 7 #13: merges approximate start-duration data onto whatever service rows are
    /// already loaded - see EventLogService.ReadServiceStartDurations for exactly what's measured.</summary>
    private async Task LoadStartDurationsAsync()
    {
        StartDurationStatus = "Scanning the System event log…";
        var durations = await Task.Run(() => _eventLog.ReadServiceStartDurations());
        var byName = durations.ToDictionary(d => d.ServiceName, StringComparer.OrdinalIgnoreCase);

        int matched = 0;
        foreach (var row in Services)
        {
            if (byName.TryGetValue(row.ServiceName, out var d))
            {
                row.StartDurationText = $"~{d.LastStartDurationMs / 1000.0:0.0}s (avg {d.AvgStartDurationMs / 1000.0:0.0}s, {d.SampleCount} samples)";
                matched++;
            }
            else
            {
                row.StartDurationText = "No recent data";
            }
        }

        StartDurationStatus = $"Found start-duration history for {matched} of {Services.Count} services (last {30} days, approximate - see tooltip).";
    }

    /// <summary>Round 7 #16: saves the current StartType/logon-account config as a JSON baseline -
    /// reuses SnapshotService/SystemSnapshot, the same file a user might otherwise capture from the
    /// Summary tab's "Save snapshot" button (that capture now includes ServiceConfigs too).</summary>
    private void CaptureConfigBaseline()
    {
        var snapshotsDir = AppPaths.GetPath("Snapshots");
        try { Directory.CreateDirectory(snapshotsDir); } catch { /* SaveFileDialog still works without a pre-created folder */ }

        var dialog = new SaveFileDialog
        {
            Title = "Save service configuration baseline",
            Filter = "Snapshot files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = $"TaskManagerPlus-ServiceBaseline-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json",
            InitialDirectory = snapshotsDir,
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            SnapshotService.Save(SnapshotService.Capture(), dialog.FileName);
            StatusMessage = $"Baseline saved: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save baseline: {ex.Message}";
        }
    }

    /// <summary>Round 7 #16: compares each currently-loaded service's StartType/LogOnAs against a
    /// saved baseline, flagging any row where either changed. A service present now but missing
    /// from the baseline (installed since) or vice versa is left alone here - that's exactly what
    /// the Summary tab's existing added/removed service diff already covers.</summary>
    private void CheckConfigDrift()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Check service configuration drift against a saved baseline",
            Filter = "Snapshot files (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = AppPaths.GetPath("Snapshots"),
        };
        if (dialog.ShowDialog() != true) return;

        var baseline = SnapshotService.Load(dialog.FileName);
        if (baseline is null)
        {
            StatusMessage = "Couldn't read that baseline file.";
            return;
        }

        var baselineByName = baseline.ServiceConfigs.ToDictionary(c => c.ServiceName, StringComparer.OrdinalIgnoreCase);
        int drifted = 0, compared = 0;
        foreach (var row in Services)
        {
            if (!baselineByName.TryGetValue(row.ServiceName, out var baselineConfig))
            {
                row.HasConfigDrift = false;
                row.ConfigDriftText = string.Empty;
                continue;
            }

            compared++;
            var changes = new List<string>();
            if (!string.Equals(baselineConfig.StartType, row.StartType.ToString(), StringComparison.OrdinalIgnoreCase))
                changes.Add($"StartType {baselineConfig.StartType} -> {row.StartType}");
            if (!string.Equals(baselineConfig.LogOnAs, row.LogOnAs, StringComparison.OrdinalIgnoreCase))
                changes.Add($"Account {baselineConfig.LogOnAs} -> {row.LogOnAs}");

            row.HasConfigDrift = changes.Count > 0;
            row.ConfigDriftText = string.Join("; ", changes);
            if (row.HasConfigDrift) drifted++;
        }

        StatusMessage = $"Compared {compared} services against baseline from {baseline.CapturedAt:g} - {drifted} changed.";
    }

    public void Dispose() => _timer.Stop();
}
