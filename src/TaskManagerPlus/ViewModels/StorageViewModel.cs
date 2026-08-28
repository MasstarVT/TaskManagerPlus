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

/// <summary>One disk selectable in the round 9 (#38) on-demand SMART-details picker.</summary>
public sealed class SmartDiskOption
{
    public int Index { get; init; }
    public string Model { get; init; } = string.Empty;
    public string Display => $"Disk {Index} — {Model}";
}

/// <summary>One label/value row in the round 9 (#38) SMART attribute table.</summary>
public sealed record SmartAttributeRow(string Label, string Value);

/// <summary>One row in the round 9 (#39) largest-files/folders scan result.</summary>
public sealed class LargestItemRow
{
    public string Path { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
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

    /// <summary>Exposed purely so StorageView.xaml can bind to the round 9 (#43) NVMe
    /// controller-vs-flash-die temperature split directly - StorageViewModel doesn't poll it
    /// itself (EnergyThermalsViewModel already does, on its own 1.5s timer).</summary>
    public EnergyThermalsViewModel EnergyThermals { get; }

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

    // Round 9, #38: on-demand full SMART attribute table, per disk.
    public ObservableCollection<SmartDiskOption> SmartDiskOptions { get; } = new();

    private SmartDiskOption? _selectedSmartDisk;
    public SmartDiskOption? SelectedSmartDisk { get => _selectedSmartDisk; set => SetProperty(ref _selectedSmartDisk, value); }

    public ObservableCollection<SmartAttributeRow> SmartDetails { get; } = new();

    private string _smartStatusText = string.Empty;
    public string SmartStatusText { get => _smartStatusText; private set => SetProperty(ref _smartStatusText, value); }

    public AsyncRelayCommand ReadSmartDetailsCommand { get; }

    // Round 9, #39: largest files/folders scanner - on-demand, depth-capped.
    private string _largestItemsRoot = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
    public string LargestItemsRoot { get => _largestItemsRoot; set => SetProperty(ref _largestItemsRoot, value); }

    public ObservableCollection<LargestItemRow> LargestItems { get; } = new();

    private bool _isScanningLargestItems;
    public bool IsScanningLargestItems { get => _isScanningLargestItems; private set => SetProperty(ref _isScanningLargestItems, value); }

    private string _largestItemsStatusText = "Not scanned";
    public string LargestItemsStatusText { get => _largestItemsStatusText; private set => SetProperty(ref _largestItemsStatusText, value); }

    public AsyncRelayCommand ScanLargestItemsCommand { get; }

    // Round 9, #41: on-demand simple sequential throughput test.
    public ObservableCollection<string> ThroughputDriveOptions { get; } = new();

    private string? _selectedThroughputDrive;
    public string? SelectedThroughputDrive { get => _selectedThroughputDrive; set => SetProperty(ref _selectedThroughputDrive, value); }

    private bool _isThroughputTesting;
    public bool IsThroughputTesting { get => _isThroughputTesting; private set => SetProperty(ref _isThroughputTesting, value); }

    private string _throughputResultText = "Not tested";
    public string ThroughputResultText { get => _throughputResultText; private set => SetProperty(ref _throughputResultText, value); }

    public AsyncRelayCommand RunThroughputTestCommand { get; }

    public StorageViewModel(PerformanceViewModel performance, EnergyThermalsViewModel energyThermals)
    {
        Performance = performance;
        EnergyThermals = energyThermals;

        var view = new ListCollectionView((IList)energyThermals.Temperatures)
        {
            Filter = o => o is SensorReading r && r.HardwareType == HardwareType.Storage,
        };
        DriveTemperatures = view;

        CheckFragmentationCommand = new AsyncRelayCommand(param => CheckFragmentationAsync(param as FragmentationRow));
        ReadSmartDetailsCommand = new AsyncRelayCommand(ReadSmartDetailsAsync, () => SelectedSmartDisk is not null);
        ScanLargestItemsCommand = new AsyncRelayCommand(ScanLargestItemsAsync, () => !IsScanningLargestItems && Directory.Exists(LargestItemsRoot));
        RunThroughputTestCommand = new AsyncRelayCommand(RunThroughputTestAsync, () => !IsThroughputTesting && SelectedThroughputDrive is not null);

        _ = Task.Run(() =>
        {
            var pools = StorageSpacesService.List();
            var fixedDrives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList();
            var hddDrives = fixedDrives
                .Select(d => d.Name.TrimEnd('\\'))
                .Where(letter => DiskFragmentationService.GetMediaType(letter) == "HDD")
                .ToList();
            var disks = SystemSpecsService.ListDisksForSmart();

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var p in pools) StoragePools.Add(p);
                foreach (var letter in hddDrives) HddVolumes.Add(new FragmentationRow { DriveLetter = letter });
                foreach (var (index, model) in disks) SmartDiskOptions.Add(new SmartDiskOption { Index = index, Model = model });
                foreach (var d in fixedDrives) ThroughputDriveOptions.Add(d.Name.TrimEnd('\\'));
                SelectedThroughputDrive = ThroughputDriveOptions.FirstOrDefault();
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
            var (success, percent, message) = await DiskFragmentationService.Analyze(row.DriveLetter);
            row.StatusText = message;
            row.IsWarning = success && percent is { } p && p >= 10;
        }
        finally
        {
            row.IsChecking = false;
        }
    }

    private async Task ReadSmartDetailsAsync()
    {
        var disk = SelectedSmartDisk;
        if (disk is null) return;

        SmartStatusText = "Reading...";
        SmartDetails.Clear();
        try
        {
            var rows = await Task.Run(() => SystemSpecsService.ReadSmartDetails(disk.Index));
            foreach (var (label, value) in rows) SmartDetails.Add(new SmartAttributeRow(label, value));
            SmartStatusText = rows.Count == 0
                ? "No SMART reliability counters reported by this drive/driver."
                : $"{rows.Count} attributes read.";
        }
        catch (Exception ex)
        {
            SmartStatusText = $"Failed: {ex.Message}";
        }
    }

    private async Task ScanLargestItemsAsync()
    {
        if (IsScanningLargestItems) return;
        string root = LargestItemsRoot;
        if (!Directory.Exists(root))
        {
            LargestItemsStatusText = "Path not found.";
            return;
        }

        IsScanningLargestItems = true;
        LargestItemsStatusText = "Scanning (depth-capped, this may take a moment on a large tree)...";
        LargestItems.Clear();
        try
        {
            var results = await Task.Run(() => LargestItemsService.Scan(root, maxDepth: 6, topN: 30));
            foreach (var item in results)
            {
                LargestItems.Add(new LargestItemRow
                {
                    Path = item.Path,
                    SizeText = Formatting.FormatBytes(item.SizeBytes),
                    Kind = item.IsDirectory ? "Folder" : "File",
                });
            }
            LargestItemsStatusText = results.Count == 0
                ? "Nothing found (or everything under this path was inaccessible)."
                : $"Top {results.Count} items under {root} (depth-capped, folders shown with their recursive total).";
        }
        catch (Exception ex)
        {
            LargestItemsStatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanningLargestItems = false;
        }
    }

    private async Task RunThroughputTestAsync()
    {
        string? drive = SelectedThroughputDrive;
        if (drive is null || IsThroughputTesting) return;

        IsThroughputTesting = true;
        ThroughputResultText = "Testing (writing then reading a temp file)...";
        try
        {
            var result = await StorageThroughputService.RunTestAsync(drive);
            ThroughputResultText = result.Message;
        }
        finally
        {
            IsThroughputTesting = false;
        }
    }
}
