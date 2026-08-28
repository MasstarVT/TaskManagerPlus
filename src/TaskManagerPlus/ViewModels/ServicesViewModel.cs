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

    /// <summary>#192/#193: service crash-loop detection (SCM 7000/7009/7024/7031/7034) and the
    /// ServicesPipeTimeout start-timeout explanation - its own Services/* instance, same
    /// no-DI-container "each ViewModel composes its own services directly" convention as _eventLog
    /// above.</summary>
    private readonly ServiceHealthEventService _serviceHealth = new();

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
    /// the same on-demand shape StabilityViewModel already uses for its own event-log query. #193
    /// extends this same pass/text with the start-timeout (7009) failure case 7036 alone can't
    /// represent - see LoadStartDurationsAsync.</summary>
    public RelayCommand LoadStartDurationsCommand { get; }

    /// <summary>#192: correlates SCM 7000/7009/7024/7031/7034 per service and badges every service
    /// this scan flags as crash-looping, plus builds CrashLoopSummary below.</summary>
    public AsyncRelayCommand ScanCrashLoopsCommand { get; }

    /// <summary>Round 7 #16: capture the current StartType/logon-account config as a baseline, or
    /// compare against a previously saved one - reuses SnapshotService/SystemSnapshot from Round 6
    /// rather than a second baseline file format.</summary>
    public RelayCommand CaptureConfigBaselineCommand { get; }
    public RelayCommand CheckConfigDriftCommand { get; }

    private string? _startDurationStatus;
    public string? StartDurationStatus { get => _startDurationStatus; set => SetProperty(ref _startDurationStatus, value); }

    /// <summary>#192: the summary card - only the services this scan actually flags as crash-
    /// looping (ServiceCrashLoopInfo.IsCrashLooping), each enriched with its `sc qfailure` recovery
    /// settings once flagged (see ScanCrashLoopsAsync). Empty until the scan has run.</summary>
    public ObservableCollection<ServiceCrashLoopInfo> CrashLoopSummary { get; } = new();

    private string? _crashLoopStatusText;
    public string? CrashLoopStatusText { get => _crashLoopStatusText; set => SetProperty(ref _crashLoopStatusText, value); }

    /// <summary>#193: what ServicesPipeTimeout is currently set to (or that it's unset, meaning
    /// Windows' 30000ms default applies) plus a plain-English explanation of what it governs -
    /// populated alongside the start-duration scan since both come from the same "why did this
    /// service start slowly/fail to start" question.</summary>
    private string? _servicesPipeTimeoutText;
    public string? ServicesPipeTimeoutText { get => _servicesPipeTimeoutText; set => SetProperty(ref _servicesPipeTimeoutText, value); }

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
        ScanCrashLoopsCommand = new AsyncRelayCommand(ScanCrashLoopsAsync);
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

    private bool CanStart() => !IsBusy && SelectedService is { CanStart: true };
    private bool CanStop() => !IsBusy && SelectedService is { CanStop: true };

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

        IsBusy = true;
        try
        {
            var (success, error) = await Task.Run(() => action(target.ServiceName));
            StatusMessage = success
                ? $"{target.DisplayName} {verbPast}."
                : $"Couldn't control {target.DisplayName}: {error}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
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
    /// already loaded - see EventLogService.ReadServiceStartDurations for exactly what's measured.
    /// #193 extends this same pass with the one failure case 7036 alone can't represent: a service
    /// that never reached "running" at all, logged instead as SCM 7009 ("did not respond in a timely
    /// fashion"). ServicesPipeTimeoutText is populated here too since both answer the same "why did
    /// this service start slowly/time out" question.</summary>
    private async Task LoadStartDurationsAsync()
    {
        StartDurationStatus = "Scanning the System event log…";
        var durations = await Task.Run(() => _eventLog.ReadServiceStartDurations());
        var byName = durations.ToDictionary(d => d.ServiceName, StringComparer.OrdinalIgnoreCase);

        // #193: the 7009 start-timeout counts, from the same SCM event family #192's crash-loop
        // scan reads - a second small scan rather than folding 7009 into ReadServiceStartDurations
        // itself, since 7036's stopped/running-pair math and 7009's plain per-service count are two
        // genuinely different measurements over two different event IDs.
        var crashLoopScan = await Task.Run(() => _serviceHealth.ReadServiceCrashLoops());
        var timeouts = crashLoopScan.Where(c => c.TimeoutCount > 0).ToDictionary(c => c.ServiceName, StringComparer.OrdinalIgnoreCase);

        var timeoutInfo = ServiceHealthEventService.ReadServiceStartTimeout();
        ServicesPipeTimeoutText = timeoutInfo.IsCustomized
            ? $"ServicesPipeTimeout is customized: {timeoutInfo.EffectiveTimeoutMs}ms. This governs how long the Service Control Manager waits for a service to report it started before giving up and logging a 7009 - it does not affect how long a service itself takes to actually finish starting."
            : $"ServicesPipeTimeout isn't set - Windows' built-in default of {timeoutInfo.EffectiveTimeoutMs}ms applies. This governs how long the Service Control Manager waits for a service to report it started before giving up and logging a 7009 - it does not affect how long a service itself takes to actually finish starting.";

        int matched = 0;
        foreach (var row in Services)
        {
            string baseText;
            if (byName.TryGetValue(row.ServiceName, out var d))
            {
                baseText = $"~{d.LastStartDurationMs / 1000.0:0.0}s (avg {d.AvgStartDurationMs / 1000.0:0.0}s, {d.SampleCount} samples)";
                matched++;
            }
            else
            {
                baseText = "No recent data";
            }

            // #193: append the start-timeout failure case, which 7036 (a stopped/running state
            // transition) never logs at all for a service that timed out instead.
            if (timeouts.TryGetValue(row.ServiceName, out var timeoutHit))
            {
                string timeoutNote = $"{timeoutHit.TimeoutCount} start-timeout(s) (SCM 7009) in last 30 days";
                baseText = baseText == "No recent data" ? timeoutNote : $"{baseText} — {timeoutNote}";
                matched++;
            }

            row.StartDurationText = baseText;
        }

        StartDurationStatus = $"Found start-duration/timeout history for {matched} of {Services.Count} services (last {30} days, approximate - see tooltip).";
    }

    /// <summary>#192: correlates SCM 7000/7009/7024/7031/7034 per service, badges every service the
    /// scan flags as crash-looping (ServiceCrashLoopInfo.IsCrashLooping), and builds CrashLoopSummary
    /// with each flagged service's `sc qfailure` recovery settings loaded alongside (reusing
    /// ServiceControlService.ReadFailureActionsTextAsync - the same shell-out the "Recovery actions"
    /// button already uses - rather than a second sc.exe-parsing path).</summary>
    private async Task ScanCrashLoopsAsync()
    {
        CrashLoopStatusText = "Scanning the System event log for service crash loops…";
        var loops = await Task.Run(() => _serviceHealth.ReadServiceCrashLoops());
        var byName = loops.ToDictionary(l => l.ServiceName, StringComparer.OrdinalIgnoreCase);

        foreach (var row in Services)
        {
            if (byName.TryGetValue(row.ServiceName, out var info) && info.IsCrashLooping)
            {
                row.IsCrashLooping = true;
                row.CrashLoopSummaryText = $"{info.TerminatedCount} unexpected termination(s), {info.TimeoutCount} start-timeout(s), {info.FailedToStartCount} failed start(s) in the last 30 days.";
            }
            else
            {
                row.IsCrashLooping = false;
                row.CrashLoopSummaryText = string.Empty;
            }
        }

        var flagged = loops.Where(l => l.IsCrashLooping).OrderByDescending(l => l.TotalCount).ToList();
        foreach (var info in flagged)
            info.RecoveryActionsText = await ServiceControlService.ReadFailureActionsTextAsync(info.ServiceName);

        CrashLoopSummary.Clear();
        foreach (var info in flagged) CrashLoopSummary.Add(info);

        CrashLoopStatusText = flagged.Count == 0
            ? "No services show repeated crashes/restarts in the last 30 days."
            : $"{flagged.Count} service(s) flagged as crash-looping in the last 30 days - quick flag, not a verdict.";
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
