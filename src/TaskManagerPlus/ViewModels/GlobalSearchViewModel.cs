using System.Collections.ObjectModel;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Cross-tab search (#100) - "find anything mentioning 'nvidia'" across Processes, Services,
/// Startup items, and the System tab's driver/software/USB-device lists at once, without hunting
/// through each tab individually. Purely a live filter over collections the other view-models are
/// already polling/have already loaded - no new sampling or I/O of its own, the same "thin
/// composition, no new poller" shape CpuViewModel/StorageViewModel/etc. already follow.
/// </summary>
public sealed class GlobalSearchViewModel : ObservableObject
{
    private const int MaxPerCategory = 12;

    private readonly ProcessesViewModel _processes;
    private readonly ServicesViewModel _services;
    private readonly StartupViewModel _startup;
    private readonly SystemSpecsViewModel _systemSpecs;

    public ObservableCollection<SearchResult> Results { get; } = new();

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) Refresh(); }
    }

    public bool HasQuery => SearchText.Trim().Length >= 2;

    public GlobalSearchViewModel(ProcessesViewModel processes, ServicesViewModel services,
        StartupViewModel startup, SystemSpecsViewModel systemSpecs)
    {
        _processes = processes;
        _services = services;
        _startup = startup;
        _systemSpecs = systemSpecs;
    }

    private static bool Has(string haystack, string needle) => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private void Refresh()
    {
        Results.Clear();
        OnPropertyChanged(nameof(HasQuery));

        var q = SearchText.Trim();
        if (q.Length < 2) return;

        foreach (var p in _processes.Processes.Where(p => Has(p.Name, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Process", Name = p.Name, Detail = $"PID {p.Pid} · {p.CpuPercent:0.0}% CPU · {Formatting.FormatBytes(p.MemoryBytes)}" });

        foreach (var s in _services.Services.Where(s => Has(s.DisplayName, q) || Has(s.ServiceName, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Service", Name = s.DisplayName, Detail = $"{s.ServiceName} · {s.Status}" });

        foreach (var s in _startup.Items.Where(s => Has(s.Name, q) || Has(s.Command, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Startup item", Name = s.Name, Detail = s.SourceDescription });

        foreach (var d in _systemSpecs.OutdatedDrivers.Where(d => Has(d.Primary, q) || Has(d.Secondary, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Driver", Name = d.Primary, Detail = d.Secondary });

        foreach (var sw in _systemSpecs.RecentlyInstalledSoftware.Where(s => Has(s.Primary, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "Software", Name = sw.Primary, Detail = sw.Secondary });

        foreach (var u in _systemSpecs.UsbDevices.Where(u => Has(u.Primary, q)).Take(MaxPerCategory))
            Results.Add(new SearchResult { Category = "USB device", Name = u.Primary, Detail = u.HealthText });
    }
}
