using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

public sealed class StartupViewModel : ObservableObject
{
    private readonly StartupManagerService _service = new();

    public ObservableCollection<StartupItem> Items { get; } = new();

    private StartupItem? _selectedItem;
    public StartupItem? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleEnabledCommand { get; }

    // #79: Scheduled Tasks - a huge, often-overlooked source of background slowdowns and
    // unwanted auto-launches the registry-Run/Startup-folder scan above doesn't cover at all.
    // Loaded on demand (a "Load scheduled tasks" button) rather than up front - enumerating every
    // registered task can take a couple of seconds on a system with hundreds of them, the same
    // "expensive, so make it explicit" tradeoff as the Stability tab's event-log query.
    public ObservableCollection<ScheduledTaskRow> ScheduledTasks { get; } = new();

    private bool _isLoadingScheduledTasks;
    public bool IsLoadingScheduledTasks { get => _isLoadingScheduledTasks; private set => SetProperty(ref _isLoadingScheduledTasks, value); }

    private ScheduledTaskRow? _selectedScheduledTask;
    public ScheduledTaskRow? SelectedScheduledTask
    {
        get => _selectedScheduledTask;
        set
        {
            var previous = _selectedScheduledTask;
            if (SetProperty(ref _selectedScheduledTask, value) && previous is not null)
            {
                // Stale delay/run-mode text from a previous selection would be misleading.
                previous.DelayText = string.Empty;
                previous.RunModeText = string.Empty;
            }
        }
    }

    public AsyncRelayCommand LoadScheduledTasksCommand { get; }
    public AsyncRelayCommand ToggleScheduledTaskCommand { get; }
    public AsyncRelayCommand CheckLogonDelayCommand { get; }

    // #19/#20: browser extension inventory and registered shell extensions - both loaded on
    // demand (a couple of hundred filesystem/registry reads apiece is more I/O than this tab's
    // live-polled sections do), the same "expensive, so make it explicit" tradeoff as Scheduled
    // Tasks above.
    public ObservableCollection<BrowserExtensionInfo> BrowserExtensions { get; } = new();
    private bool _isLoadingBrowserExtensions;
    public bool IsLoadingBrowserExtensions { get => _isLoadingBrowserExtensions; private set => SetProperty(ref _isLoadingBrowserExtensions, value); }
    public AsyncRelayCommand LoadBrowserExtensionsCommand { get; }

    public ObservableCollection<ShellExtensionInfo> ShellExtensions { get; } = new();
    private bool _isLoadingShellExtensions;
    public bool IsLoadingShellExtensions { get => _isLoadingShellExtensions; private set => SetProperty(ref _isLoadingShellExtensions, value); }
    public AsyncRelayCommand LoadShellExtensionsCommand { get; }

    // #89/#90: boot time breakdown for the most recent boot, plus a small self-recorded trend
    // across sessions - see BootPerformanceService's remarks on why the breakdown is an adaptive
    // field list rather than fixed named properties.
    private BootTimeBreakdown? _bootBreakdown;
    public BootTimeBreakdown? BootBreakdown { get => _bootBreakdown; private set => SetProperty(ref _bootBreakdown, value); }

    public ObservableCollection<double> BootHistoryMs { get; } = new();
    private readonly LineSeries<double> _bootHistoryLine;
    public ISeries[] BootHistorySeries { get; }
    public Axis[] BootHistoryXAxes { get; }
    public Axis[] BootHistoryYAxes { get; }

    public StartupViewModel()
    {
        _bootHistoryLine = new LineSeries<double>
        {
            Values = BootHistoryMs,
            Stroke = new SolidColorPaint(SKColors.DeepSkyBlue, 2f),
            Fill = null,
            GeometrySize = 6,
            LineSmoothness = 0.2,
        };
        BootHistorySeries = new ISeries[] { _bootHistoryLine };
        BootHistoryXAxes = new[] { new Axis { IsVisible = false, ShowSeparatorLines = false } };
        BootHistoryYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => $"{v / 1000.0:0.#}s",
                LabelsPaint = new SolidColorPaint(new SKColor(0x9A, 0x9A, 0xA2)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(0x33, 0x33, 0x3A, 160)) { StrokeThickness = 1 },
            },
        };

        RefreshCommand = new RelayCommand(_ => Refresh());
        ToggleEnabledCommand = new RelayCommand(param => Toggle(param as StartupItem ?? SelectedItem));

        LoadScheduledTasksCommand = new AsyncRelayCommand(LoadScheduledTasksAsync);
        ToggleScheduledTaskCommand = new AsyncRelayCommand(param => ToggleScheduledTaskAsync(param as ScheduledTaskRow ?? SelectedScheduledTask));
        CheckLogonDelayCommand = new AsyncRelayCommand(CheckLogonDelayAsync, () => SelectedScheduledTask is not null);

        LoadBrowserExtensionsCommand = new AsyncRelayCommand(LoadBrowserExtensionsAsync);
        LoadShellExtensionsCommand = new AsyncRelayCommand(LoadShellExtensionsAsync);

        Refresh();
        LoadBootPerformance();
    }

    private async Task LoadBrowserExtensionsAsync()
    {
        IsLoadingBrowserExtensions = true;
        try
        {
            var extensions = await Task.Run(BrowserExtensionService.List);
            BrowserExtensions.Clear();
            foreach (var e in extensions) BrowserExtensions.Add(e);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't load browser extensions: {ex.Message}";
        }
        finally
        {
            IsLoadingBrowserExtensions = false;
        }
    }

    private async Task LoadShellExtensionsAsync()
    {
        IsLoadingShellExtensions = true;
        try
        {
            var extensions = await Task.Run(ShellExtensionService.List);
            ShellExtensions.Clear();
            foreach (var e in extensions) ShellExtensions.Add(e);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't load shell extensions: {ex.Message}";
        }
        finally
        {
            IsLoadingShellExtensions = false;
        }
    }

    public void ApplyAxisTheme(System.Windows.Media.Color text, System.Windows.Media.Color separator)
    {
        var textSk = new SKColor(text.R, text.G, text.B);
        var sepSk = new SKColor(separator.R, separator.G, separator.B, separator.A);
        BootHistoryYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        BootHistoryYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
    }

    private void LoadBootPerformance()
    {
        _ = Task.Run(() =>
        {
            var breakdown = BootPerformanceService.ReadLatest();
            var history = BootPerformanceService.RecordAndLoadHistory(breakdown);

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                BootBreakdown = breakdown;
                BootHistoryMs.Clear();
                foreach (var h in history) BootHistoryMs.Add(h.TotalMs);
            });
        });
    }

    private async Task LoadScheduledTasksAsync()
    {
        IsLoadingScheduledTasks = true;
        try
        {
            var tasks = await ScheduledTaskService.ListAsync();
            ScheduledTasks.Clear();
            foreach (var t in tasks) ScheduledTasks.Add(t);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't load scheduled tasks: {ex.Message}";
        }
        finally
        {
            IsLoadingScheduledTasks = false;
        }
    }

    private async Task ToggleScheduledTaskAsync(ScheduledTaskRow? task)
    {
        if (task is null) return;

        bool newState = !task.IsEnabled;
        var (success, error) = await ScheduledTaskService.SetEnabledAsync(task.Name, newState);
        if (success)
        {
            task.IsEnabled = newState;
            StatusMessage = $"{task.Name} {(newState ? "enabled" : "disabled")}.";
        }
        else
        {
            StatusMessage = $"Couldn't change {task.Name}: {error}";
        }
    }

    private async Task CheckLogonDelayAsync()
    {
        var target = SelectedScheduledTask;
        if (target is null) return;

        try
        {
            var (delayText, runModeText) = await ScheduledTaskService.ReadLogonTriggerInfoAsync(target.Name);
            target.DelayText = delayText;
            target.RunModeText = runModeText;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't check logon delay for {target.Name}: {ex.Message}";
        }
    }

    private void Refresh()
    {
        Items.Clear();
        var items = _service.Sample();
        foreach (var item in items)
            Items.Add(item);

        // #91/#22: measured startup delay + combined impact score, off the UI thread (Process
        // enumeration + per-process StartTime/CPU/memory reads) - applied back via Dispatcher, the
        // same pattern StorageViewModel's background WMI queries use.
        _ = Task.Run(() =>
        {
            var measurements = StartupDelayService.ComputeDelays(items);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var (item, measurement) in measurements)
                {
                    item.MeasuredDelayText = measurement.DelayText;
                    item.ImpactText = measurement.ImpactText;
                    item.ImpactDetailText = measurement.ImpactDetailText;
                }
            });
        });

        // #18: signed/unsigned trust badge, reusing SignatureCheckService's shared per-path cache
        // (extracted from ProcessMonitorService in Round 2) rather than a duplicate check - also
        // off the UI thread, since a first-time signature check reads the file from disk.
        _ = Task.Run(() =>
        {
            var statuses = items.ToDictionary(item => item, item => SignatureCheckService.GetStatus(StartupManagerService.ExtractPath(item.Command)));
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var (item, status) in statuses) item.SignatureStatus = status;
            });
        });
    }

    private void Toggle(StartupItem? item)
    {
        if (item is null) return;

        bool wasEnabled = item.IsEnabled;
        bool newState = !wasEnabled;
        var (success, error) = StartupManagerService.SetEnabled(item, newState);
        if (success)
        {
            item.IsEnabled = newState;
            StatusMessage = $"{item.Name} {(newState ? "enabled" : "disabled")}. Takes effect on next sign-in.";
        }
        else
        {
            StatusMessage = $"Couldn't change {item.Name}: {error}";
        }

        // #972: record every mutation this app performs - see ChangeJournalService's remarks.
        // No RegistryKeyToBackup/backup here - #971 wires that ahead of the remediation-flow's
        // own disable-startup-item action specifically, not this plain tab toggle.
        ChangeJournalService.Append(new ChangeJournalEntry
        {
            Kind = ChangeKind.StartupToggle,
            Target = item.Name,
            ActionDescription = newState ? "Enabled at startup" : "Disabled at startup",
            BeforeValue = wasEnabled ? "Enabled" : "Disabled",
            AfterValue = success ? (newState ? "Enabled" : "Disabled") : (wasEnabled ? "Enabled" : "Disabled"),
            TriggeredBy = "Startup tab",
            Success = success,
            IsUndoable = success,
            StartupItemName = item.Name,
            StartupItemCommand = item.Command,
            StartupItemSource = item.Source.ToString(),
        });
    }
}
