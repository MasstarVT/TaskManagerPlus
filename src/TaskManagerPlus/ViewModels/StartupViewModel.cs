using System.Collections.ObjectModel;
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
                previous.DelayText = string.Empty; // stale delay from a previous selection would be misleading
        }
    }

    public AsyncRelayCommand LoadScheduledTasksCommand { get; }
    public RelayCommand ToggleScheduledTaskCommand { get; }
    public AsyncRelayCommand CheckLogonDelayCommand { get; }

    public StartupViewModel()
    {
        RefreshCommand = new RelayCommand(_ => Refresh());
        ToggleEnabledCommand = new RelayCommand(param => Toggle(param as StartupItem ?? SelectedItem));

        LoadScheduledTasksCommand = new AsyncRelayCommand(LoadScheduledTasksAsync);
        ToggleScheduledTaskCommand = new RelayCommand(param => ToggleScheduledTask(param as ScheduledTaskRow ?? SelectedScheduledTask));
        CheckLogonDelayCommand = new AsyncRelayCommand(CheckLogonDelayAsync, () => SelectedScheduledTask is not null);

        Refresh();
    }

    private async Task LoadScheduledTasksAsync()
    {
        IsLoadingScheduledTasks = true;
        try
        {
            var tasks = await Task.Run(ScheduledTaskService.List);
            ScheduledTasks.Clear();
            foreach (var t in tasks) ScheduledTasks.Add(t);
        }
        finally
        {
            IsLoadingScheduledTasks = false;
        }
    }

    private void ToggleScheduledTask(ScheduledTaskRow? task)
    {
        if (task is null) return;

        bool newState = !task.IsEnabled;
        var (success, error) = ScheduledTaskService.SetEnabled(task.Name, newState);
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

        target.DelayText = await Task.Run(() => ScheduledTaskService.ReadLogonDelay(target.Name));
    }

    private void Refresh()
    {
        Items.Clear();
        foreach (var item in _service.Sample())
            Items.Add(item);
    }

    private void Toggle(StartupItem? item)
    {
        if (item is null) return;

        bool newState = !item.IsEnabled;
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
    }
}
