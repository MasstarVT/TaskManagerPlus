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

/// <summary>One tile in the round 10 (#306) critical-attribute triage strip - "quick flag, not a
/// verdict": a non-zero reallocated/pending/uncorrectable/CRC count is worth watching, not on its
/// own a reason to replace a drive.</summary>
public sealed class SmartTriageTile
{
    public string Title { get; init; } = string.Empty;
    public string ValueText { get; init; } = "—";
    public bool IsCritical { get; init; }
}

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

    // Round 10, #301-#312: on-demand raw ATA SMART attribute table (the real 30-entry table from
    // MSStorageDriver_ATAPISmartData, not just the driver-summarised MSFT_StorageReliabilityCounter
    // fields above) plus derived triage/temperature/power-on/wear-cycle/endurance summaries - all
    // populated from the same ReadSmartDetailsCommand action above rather than a separate polling
    // loop, per SmartRawAttributeService.Read.
    public ObservableCollection<SmartRawAttribute> SmartRawAttributes { get; } = new();

    private string _smartRawStatusText = "Not read";
    public string SmartRawStatusText { get => _smartRawStatusText; private set => SetProperty(ref _smartRawStatusText, value); }

    private string _smartVendorProfileText = string.Empty;
    public string SmartVendorProfileText { get => _smartVendorProfileText; private set => SetProperty(ref _smartVendorProfileText, value); }

    private string _smartAvailabilityNote = string.Empty;
    public string SmartAvailabilityNote { get => _smartAvailabilityNote; private set => SetProperty(ref _smartAvailabilityNote, value); }

    // #306: five-tile triage strip - pinned above the raw attribute grid.
    public ObservableCollection<SmartTriageTile> SmartTriageTiles { get; } = new();

    // #307
    private string _smartTemperatureSummary = string.Empty;
    public string SmartTemperatureSummary { get => _smartTemperatureSummary; private set => SetProperty(ref _smartTemperatureSummary, value); }

    // #308
    private string _smartPowerOnSummary = string.Empty;
    public string SmartPowerOnSummary { get => _smartPowerOnSummary; private set => SetProperty(ref _smartPowerOnSummary, value); }

    // #309: HDD-only load/unload + start/stop wear card - hidden entirely for SSD/NVMe media.
    private bool _showSmartWearCycles;
    public bool ShowSmartWearCycles { get => _showSmartWearCycles; private set => SetProperty(ref _showSmartWearCycles, value); }

    private string _smartLoadUnloadText = string.Empty;
    public string SmartLoadUnloadText { get => _smartLoadUnloadText; private set => SetProperty(ref _smartLoadUnloadText, value); }

    private string _smartStartStopText = string.Empty;
    public string SmartStartStopText { get => _smartStartStopText; private set => SetProperty(ref _smartStartStopText, value); }

    // #310/#311: Endurance card - TBW/WAF estimate + SSD wear-levelling detail cross-checked
    // against the driver's own Wear% summary.
    private bool _showSmartEndurance;
    public bool ShowSmartEndurance { get => _showSmartEndurance; private set => SetProperty(ref _showSmartEndurance, value); }

    private string _smartEnduranceTbwText = string.Empty;
    public string SmartEnduranceTbwText { get => _smartEnduranceTbwText; private set => SetProperty(ref _smartEnduranceTbwText, value); }

    private string _smartEnduranceWafText = string.Empty;
    public string SmartEnduranceWafText { get => _smartEnduranceWafText; private set => SetProperty(ref _smartEnduranceWafText, value); }

    private string _smartWearLevelText = string.Empty;
    public string SmartWearLevelText { get => _smartWearLevelText; private set => SetProperty(ref _smartWearLevelText, value); }

    private string _smartWearWarningText = string.Empty;
    public string SmartWearWarningText { get => _smartWearWarningText; private set => SetProperty(ref _smartWearWarningText, value); }

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
        catch (Exception ex)
        {
            row.StatusText = $"Failed: {ex.Message}";
            row.IsWarning = false;
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
        ResetSmartRawDisplay();
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

        // Round 10, #301-#312: the raw attribute table + derived summaries, tied into this same
        // on-demand action rather than a separate polling loop.
        SmartRawStatusText = "Reading raw SMART attributes...";
        try
        {
            var result = await Task.Run(() => SmartRawAttributeService.Read(disk.Index, disk.Model));
            ApplySmartRawResult(result);
        }
        catch (Exception ex)
        {
            SmartRawStatusText = $"Failed: {ex.Message}";
        }
    }

    private void ResetSmartRawDisplay()
    {
        SmartRawAttributes.Clear();
        SmartTriageTiles.Clear();
        SmartVendorProfileText = string.Empty;
        SmartAvailabilityNote = string.Empty;
        SmartTemperatureSummary = string.Empty;
        SmartPowerOnSummary = string.Empty;
        ShowSmartWearCycles = false;
        SmartLoadUnloadText = string.Empty;
        SmartStartStopText = string.Empty;
        ShowSmartEndurance = false;
        SmartEnduranceTbwText = string.Empty;
        SmartEnduranceWafText = string.Empty;
        SmartWearLevelText = string.Empty;
        SmartWearWarningText = string.Empty;
    }

    private void ApplySmartRawResult(SmartRawResult result)
    {
        SmartVendorProfileText = result.VendorProfileName is null
            ? "No vendor-specific attribute map matched this drive's model - generic ATA-8 attribute names shown."
            : $"Vendor profile: {result.VendorProfileName} (attribute names/raw decoding for reassigned IDs use this vendor's convention).";

        if (result.Unavailable)
        {
            SmartRawStatusText = "No raw SMART data available.";
            SmartAvailabilityNote = result.UnavailableReason;
            return;
        }

        SmartAvailabilityNote = $"Source: {result.SourceDescription}.";
        foreach (var attr in result.Attributes) SmartRawAttributes.Add(attr);
        SmartRawStatusText = result.Attributes.Count == 0
            ? "The SMART data blob was read but contained no populated attribute entries."
            : $"{result.Attributes.Count} raw attributes decoded, sorted by margin to failure.";

        BuildTriageTiles(result.Attributes);
        SmartTemperatureSummary = BuildTemperatureSummary(result.Attributes);
        SmartPowerOnSummary = BuildPowerOnSummary(result.Attributes);
        BuildWearCycleSummary(result.Attributes, result.MediaType);
        BuildEnduranceSummary(result.Attributes, result.DriverWearPercent, result.BytesPerSector);
    }

    private static SmartRawAttribute? Find(IReadOnlyList<SmartRawAttribute> attrs, byte id) => attrs.FirstOrDefault(a => a.Id == id);

    /// <summary>#306: five fixed tiles for the attributes that actually predict failure.
    /// Informational only - see the "quick flag, not a verdict" caption in StorageView.xaml.</summary>
    private void BuildTriageTiles(IReadOnlyList<SmartRawAttribute> attrs)
    {
        (byte Id, string Title)[] triage =
        {
            (0x05, "Reallocated"),
            (0xC5, "Current Pending"),
            (0xC6, "Offline Uncorr."),
            (0xBB, "Reported Uncorr."),
            (0xC7, "UDMA CRC"),
        };
        foreach (var (id, title) in triage)
        {
            var attr = Find(attrs, id);
            SmartTriageTiles.Add(new SmartTriageTile
            {
                Title = title,
                ValueText = attr is null ? "—" : attr.RawValue.ToString("N0"),
                IsCritical = attr is not null && attr.RawValue != 0,
            });
        }
    }

    /// <summary>#307: attributes 0xC2/0xBE pack current/lifetime-min/lifetime-max temperature as
    /// three 16-bit little-endian words on most drives - a best-effort decode (word layout varies
    /// by vendor/firmware, so this is captioned as such), not the live LibreHardwareMonitor sensor
    /// the drive-temperature card above already charts.</summary>
    private static string BuildTemperatureSummary(IReadOnlyList<SmartRawAttribute> attrs)
    {
        var attr = Find(attrs, 0xC2) ?? Find(attrs, 0xBE);
        if (attr is null) return string.Empty;

        int current = attr.RawBytes[0];
        int min = attr.RawBytes[2] | (attr.RawBytes[3] << 8);
        int max = attr.RawBytes[4] | (attr.RawBytes[5] << 8);

        if (min == 0 && max == 0)
            return $"Now {current}°C (attribute {attr.IdHex}) - lifetime min/max not reported by this drive's firmware.";
        return $"Now {current}°C / Lifetime min {min}°C / Lifetime max {max}°C (attribute {attr.IdHex}, best-effort word decode - layout varies by vendor).";
    }

    /// <summary>#308: converts Power-On Hours (0x09) into a rough calendar duration and pairs it
    /// with Power Cycle Count (0x0C) to derive an average hours-per-cycle figure.</summary>
    private static string BuildPowerOnSummary(IReadOnlyList<SmartRawAttribute> attrs)
    {
        var hoursAttr = Find(attrs, 0x09);
        if (hoursAttr is null) return string.Empty;

        uint hours = (uint)(hoursAttr.RawBytes[0] | (hoursAttr.RawBytes[1] << 8) | (hoursAttr.RawBytes[2] << 16) | (hoursAttr.RawBytes[3] << 24));
        int years = (int)(hours / 8760);
        int months = (int)((hours % 8760) / 730);
        string duration = years > 0 ? $"{years} y {months} mo" : months > 0 ? $"{months} mo" : $"{hours} h";
        string text = $"{duration} ({hours:N0} h) power-on";

        var cyclesAttr = Find(attrs, 0x0C);
        if (cyclesAttr is not null)
        {
            uint cycles = (uint)(cyclesAttr.RawBytes[0] | (cyclesAttr.RawBytes[1] << 8) | (cyclesAttr.RawBytes[2] << 16) | (cyclesAttr.RawBytes[3] << 24));
            text += cycles > 0
                ? $" · {cycles:N0} power cycles · avg {(double)hours / cycles:0.#} h/cycle"
                : " · 0 power cycles recorded";
        }
        return text;
    }

    /// <summary>#309: Load/Unload Cycle Count (0xC1) and Start/Stop Count (0x04) against this
    /// vendor's typical (not per-model) rated maximum - HDD-only, hidden entirely for SSD/NVMe
    /// where head-parking cycles aren't a meaningful wear metric.</summary>
    private void BuildWearCycleSummary(IReadOnlyList<SmartRawAttribute> attrs, string mediaType)
    {
        if (mediaType != "HDD") return;

        var loadUnload = Find(attrs, 0xC1);
        var startStop = Find(attrs, 0x04);
        if (loadUnload is null && startStop is null) return;

        var profile = SmartVendorProfiles.Match(SelectedSmartDisk?.Model ?? string.Empty);
        ShowSmartWearCycles = true;
        SmartLoadUnloadText = FormatCycleAgainstRating(loadUnload, profile?.TypicalLoadUnloadRatedMax);
        SmartStartStopText = FormatCycleAgainstRating(startStop, profile?.TypicalStartStopRatedMax);
    }

    private static string FormatCycleAgainstRating(SmartRawAttribute? attr, int? typicalRatedMax)
    {
        if (attr is null) return "Not reported";
        string countText = attr.RawValue.ToString("N0");
        if (typicalRatedMax is null) return $"{countText} (no typical rating available for this vendor)";

        double fraction = attr.RawValue / (double)typicalRatedMax.Value;
        string flag = fraction >= 0.8 ? " ⚠ approaching typical rated life" : string.Empty;
        return $"{countText} / ~{typicalRatedMax.Value:N0} typical rated max{flag}";
    }

    /// <summary>#310/#311: host TBW + an approximate write-amplification factor where a
    /// NAND-writes attribute also exists, plus SSD wear-levelling detail cross-checked against the
    /// driver's own MSFT_StorageReliabilityCounter.Wear summary. Shown only when at least one
    /// endurance-family attribute is present (effectively SSD/NVMe media).</summary>
    private void BuildEnduranceSummary(IReadOnlyList<SmartRawAttribute> attrs, int? driverWearPercent, int bytesPerSector)
    {
        var hostWritten = Find(attrs, 0xF1) ?? Find(attrs, 0xF9);
        var nandWritten = Find(attrs, 0xF5) ?? Find(attrs, 0xE2);
        var wearLeveling = Find(attrs, 0xAD);
        var avgErase = Find(attrs, 0xB1);
        var maxErase = Find(attrs, 0xE8);
        var lifeLeft = Find(attrs, 0xE9) ?? Find(attrs, 0xE7);

        if (hostWritten is null && wearLeveling is null && lifeLeft is null && avgErase is null && maxErase is null)
            return; // no endurance-family attributes at all - not an SSD, or this driver doesn't report them

        ShowSmartEndurance = true;

        if (hostWritten is not null)
        {
            double bytesWritten = hostWritten.RawValue * (double)bytesPerSector;
            SmartEnduranceTbwText = $"{Formatting.FormatBytes(bytesWritten)} written over the drive's life (host writes, attribute {hostWritten.IdHex}).";

            if (nandWritten is not null && hostWritten.RawValue > 0)
            {
                double waf = nandWritten.RawValue / (double)hostWritten.RawValue;
                SmartEnduranceWafText = $"Write amplification ≈ {waf:0.00}x (estimate - depends on a vendor-specific NAND-writes attribute pair, {nandWritten.IdHex}/{hostWritten.IdHex}).";
            }
        }
        else
        {
            SmartEnduranceTbwText = "Host LBAs Written not reported by this drive/driver.";
        }

        if (wearLeveling is not null || avgErase is not null || maxErase is not null || lifeLeft is not null)
        {
            var parts = new List<string>();
            if (wearLeveling is not null) parts.Add($"Wear leveling {wearLeveling.Current}");
            if (avgErase is not null) parts.Add($"Avg. block erase {avgErase.RawDisplay}");
            if (maxErase is not null) parts.Add($"Max block erase {maxErase.RawDisplay}");
            if (lifeLeft is not null) parts.Add($"Life left {lifeLeft.Current}%");
            SmartWearLevelText = string.Join(" · ", parts);

            if (lifeLeft is not null && driverWearPercent.HasValue)
            {
                int derivedWear = 100 - lifeLeft.Current;
                int delta = Math.Abs(derivedWear - driverWearPercent.Value);
                SmartWearWarningText = delta > 10
                    ? $"Raw attributes imply ~{derivedWear}% worn, but the driver's own Wear summary reports {driverWearPercent.Value}% - usually a stale driver summary, not a real disagreement. Quick flag, not a verdict."
                    : string.Empty;
            }
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
        catch (Exception ex)
        {
            ThroughputResultText = $"Test failed: {ex.Message}";
        }
        finally
        {
            IsThroughputTesting = false;
        }
    }
}
