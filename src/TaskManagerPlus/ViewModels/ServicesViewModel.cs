using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

public sealed class ServicesViewModel : ObservableObject, IDisposable
{
    private readonly ServiceControlService _service = new();
    private readonly DispatcherTimer _timer;
    private bool _isRefreshing;
    private bool _isBusy;

    public ObservableCollection<ServiceRow> Services { get; } = new();
    public ICollectionView ServicesView { get; }

    private ServiceRow? _selectedService;
    public ServiceRow? SelectedService
    {
        get => _selectedService;
        set => SetProperty(ref _selectedService, value);
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

    public ServicesViewModel()
    {
        ServicesView = CollectionViewSource.GetDefaultView(Services);
        ServicesView.Filter = FilterPredicate;

        StartCommand = new RelayCommand(_ => _ = RunAction(ServiceControlService.Start, "started"), _ => CanStart());
        StopCommand = new RelayCommand(_ => _ = RunAction(ServiceControlService.Stop, "stopped"), _ => CanStop());
        RestartCommand = new RelayCommand(_ => _ = RunAction(ServiceControlService.Restart, "restarted"), _ => CanStop());
        RefreshNowCommand = new RelayCommand(_ => _ = RefreshAsync());

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
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

    public void Dispose() => _timer.Stop();
}
