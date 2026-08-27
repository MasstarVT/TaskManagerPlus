using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using LibreHardwareMonitor.Hardware;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>One fixed volume's on-demand HDD fragmentation check (#86) - a row per fixed drive
/// that reports as an HDD (SSDs are hidden entirely, since fragmentation isn't meaningful there).</summary>
public sealed class FragmentationRow : ObservableObject
{
    public string DriveLetter { get; init; } = string.Empty;

    private string _statusText = "Not checked";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private bool _isChecking;
    public bool IsChecking { get => _isChecking; set => SetProperty(ref _isChecking, value); }

    private bool _isWarning;
    public bool IsWarning { get => _isWarning; set => SetProperty(ref _isWarning, value); }
}

/// <summary>
/// Backs the Storage tab. Thin composition over the shared PerformanceViewModel sampler -
/// see CpuViewModel's remarks for why this doesn't own its own timer. Also takes the shared
/// EnergyThermalsViewModel purely to re-present its already-polled Temperatures list filtered
/// down to Storage hardware (#17) - no new sensor sampling here.
/// </summary>
public sealed class StorageViewModel : ObservableObject
{
    public PerformanceViewModel Performance { get; }

    /// <summary>Per-drive temperature readings (#17), filtered from EnergyThermalsViewModel's
    /// already-polled Temperatures collection. A fresh ListCollectionView (not
    /// CollectionViewSource.GetDefaultView, which would return the same shared default view the
    /// Energy &amp; Thermals tab's own binding uses, and setting a Filter on it would wrongly
    /// filter that tab's list too) so the two tabs' views of the same underlying collection stay
    /// independent.</summary>
    public ICollectionView DriveTemperatures { get; }

    // #85: Storage Spaces / RAID member health rollup - queried once (like CpuTopologyService's
    // static topology query), not on a timer, since pool membership can't change without an
    // explicit admin action. Empty (and the card hidden) on the large majority of systems that
    // don't use Storage Spaces at all.
    public ObservableCollection<StorageSpaceInfo> StoragePools { get; } = new();

    // #86: fixed HDD volumes eligible for an on-demand fragmentation check - SSDs never appear
    // here at all (fragmentation isn't a meaningful concept for them).
    public ObservableCollection<FragmentationRow> HddVolumes { get; } = new();
    public AsyncRelayCommand CheckFragmentationCommand { get; }

    public StorageViewModel(PerformanceViewModel performance, EnergyThermalsViewModel energyThermals)
    {
        Performance = performance;

        var view = new ListCollectionView((IList)energyThermals.Temperatures)
        {
            Filter = o => o is SensorReading r && r.HardwareType == HardwareType.Storage,
        };
        DriveTemperatures = view;

        CheckFragmentationCommand = new AsyncRelayCommand(param => CheckFragmentationAsync(param as FragmentationRow));

        _ = Task.Run(() =>
        {
            var pools = StorageSpacesService.List();
            var hddDrives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.Name.TrimEnd('\\'))
                .Where(letter => DiskFragmentationService.GetMediaType(letter) == "HDD")
                .ToList();

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var p in pools) StoragePools.Add(p);
                foreach (var letter in hddDrives) HddVolumes.Add(new FragmentationRow { DriveLetter = letter });
            });
        });
    }

    private async Task CheckFragmentationAsync(FragmentationRow? row)
    {
        if (row is null || row.IsChecking) return;
        row.IsChecking = true;
        row.StatusText = "Analyzing (this can take a while on a large drive)...";
        try
        {
            var (success, percent, message) = await Task.Run(() => DiskFragmentationService.Analyze(row.DriveLetter));
            row.StatusText = message;
            row.IsWarning = success && percent is { } p && p >= 10;
        }
        finally
        {
            row.IsChecking = false;
        }
    }
}
