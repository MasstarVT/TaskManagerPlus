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

    public StartupViewModel()
    {
        RefreshCommand = new RelayCommand(_ => Refresh());
        ToggleEnabledCommand = new RelayCommand(param => Toggle(param as StartupItem ?? SelectedItem));

        Refresh();
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
