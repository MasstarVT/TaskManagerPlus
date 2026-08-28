using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the GPU tab (#53-56, #670-677). Unlike CPU/Memory/Storage/Network (thin wrappers over the
/// shared PerformanceViewModel/HardwareMonitorService sampler), this owns its own GpuMonitorService and
/// DispatcherTimer - GPU Engine/GPU Adapter Memory perf-counter enumeration is a genuinely separate,
/// heavier data source (dynamic instance discovery every tick, not a fixed counter array), the same
/// "doesn't fit the shared sampler" reasoning EnergyThermalsViewModel already documents for its own
/// independent LibreHardwareMonitorLib poll.
///
/// #670-677 add a second, on-demand data source alongside the 1s live poll above - TDR/device-removed
/// event-log scans, a `pnputil`-based driver-version history, and an optional `nvidia-smi` shell-out -
/// loaded once at startup plus a manual refresh button (LoadGpuEventHistoryCommand), the same
/// on-demand-vs-polled split every other event-log-backed tab in this app already follows. The TDR
/// registry readout (#671/#672) is a plain synchronous registry read (same cost tier as
/// GpuMonitorService's own WMI adapter-identity read in its constructor), so that one runs eagerly
/// with no button of its own.
/// </summary>
public sealed class GpuViewModel : ObservableObject, IDisposable
{
    private const int HistoryLength = 60;

    private readonly GpuMonitorService _service = new();
    private readonly EventLogService _eventLog = new();
    private readonly DispatcherTimer _timer;
    private bool _isRefreshing;
    private readonly EnergyThermalsViewModel _energyThermals;
    private readonly ProcessesViewModel _processes;

    /// <summary>#678: CPU core-saturation evidence for the bottleneck verdict below - the shared
    /// sampler every other tab already reads from, not a new poll.</summary>
    private readonly PerformanceViewModel _performance;

    public bool IsAvailable => _service.IsAvailable;

    /// <summary>#608: exposes the shared EnergyThermalsViewModel instance so GpuView.xaml can bind
    /// directly to EnergyThermals.GpuThermalHeadroomC/GpuTempC without new cross-ViewModel plumbing -
    /// same shape as CpuViewModel.EnergyThermals. #675's GpuMemoryJunctionTempC is exposed the same
    /// way.</summary>
    public EnergyThermalsViewModel EnergyThermals => _energyThermals;

    public ObservableCollection<GpuAdapterIdentity> InstalledAdapters { get; } = new();
    public ObservableCollection<GpuAdapterSnapshot> LiveAdapters { get; } = new();

    /// <summary>#56: "which app is using it" - the same per-process GPU% Round 4 already samples
    /// (ProcessMonitorService.ReadGpuUsageByPid), just re-presented live-sorted here rather than
    /// sampled a second time - the same "second ICollectionView over one shared collection" pattern
    /// MemoryViewModel.TopMemoryProcesses already established.</summary>
    public ICollectionView TopGpuProcesses { get; }

    // #253 (nice-to-have): a short refresh-rate summary line, reusing the same
    // DisplayModeService.ReadAudit the Responsiveness tab's own display-mode card is built from -
    // the Responsiveness tab card is the primary home for this item; this is just a pointer so a
    // user looking at GPU output doesn't have to switch tabs to see it. Loaded once at start-up
    // (a fast EnumDisplayDevices/EnumDisplaySettingsEx read, not worth a per-tick timer).
    private string _refreshRateSummaryText = string.Empty;
    public string RefreshRateSummaryText { get => _refreshRateSummaryText; private set => SetProperty(ref _refreshRateSummaryText, value); }

    // ================================================================================
    // #671/#672: TDR registry configuration + Hardware-accelerated GPU Scheduling state - plain
    // synchronous registry reads (see class remarks), refreshed at startup and again whenever
    // LoadGpuEventHistoryCommand runs (in case the user edited the registry and hit refresh).
    // ================================================================================
    private GpuTdrRegistrySettings _tdrRegistrySettings = new();
    public GpuTdrRegistrySettings TdrRegistrySettings { get => _tdrRegistrySettings; private set => SetProperty(ref _tdrRegistrySettings, value); }

    private GpuHagsInfo _hagsInfo = new();
    public GpuHagsInfo HagsInfo { get => _hagsInfo; private set => SetProperty(ref _hagsInfo, value); }

    // ================================================================================
    // #670/#677: TDR event detail, DXGI device-removed crashes, and the unrecovered-reset count -
    // one combined on-demand load (LoadGpuEventHistoryCommand), same "button + startup load" shape
    // as StabilityViewModel's WHEA card.
    // ================================================================================
    public ObservableCollection<GpuTdrEvent> TdrEvents { get; } = new();
    public ObservableCollection<GpuDeviceRemovedEvent> DeviceRemovedEvents { get; } = new();

    private int _unrecoveredResetCount;
    public int UnrecoveredResetCount { get => _unrecoveredResetCount; private set => SetProperty(ref _unrecoveredResetCount, value); }

    public AsyncRelayCommand LoadGpuEventHistoryCommand { get; }

    private string _gpuEventHistoryStatusText = "Not checked yet this session - click \"Check GPU events\".";
    public string GpuEventHistoryStatusText { get => _gpuEventHistoryStatusText; private set => SetProperty(ref _gpuEventHistoryStatusText, value); }

    // ================================================================================
    // #673: driver-version-to-crash correlation - derived from the same TDR/crash events above
    // plus a `pnputil`-sourced driver-version history, computed as part of the same on-demand load.
    // ================================================================================
    public ObservableCollection<GpuDriverVersionBucket> DriverVersionBuckets { get; } = new();

    // ================================================================================
    // #674: VRAM pressure - dedicated-usage-vs-installed-capacity plus a shared-VRAM ("spillover")
    // trend, from the perf-counter data GpuMonitorService already samples every tick (see
    // GpuAdapterSnapshot.TotalCommittedBytes' remarks for exactly which counters this is built from
    // and why it's labeled "Committed", not "Budget").
    // ================================================================================
    public ObservableCollection<double> VramSpilloverHistory { get; } = NewHistory();
    private readonly LineSeries<double> _spilloverGlow;
    private readonly LineSeries<double> _spilloverCore;
    public ISeries[] VramSpilloverSeries { get; }
    public Axis[] VramSpilloverXAxes { get; }
    public Axis[] VramSpilloverYAxes { get; }

    // A sustained-spillover verdict needs more than one high sample - a momentary shader-compile
    // spike shouldn't read the same as minutes of steady PCIe paging.
    private const long SpilloverThresholdBytes = 256L * 1024 * 1024; // 256 MB
    private const int SpilloverSustainTicks = 10; // ~10s at this tab's 1s tick interval
    private readonly Queue<bool> _spilloverSustainWindow = new();

    private string _vramPressureVerdictText = string.Empty;
    public string VramPressureVerdictText { get => _vramPressureVerdictText; private set => SetProperty(ref _vramPressureVerdictText, value); }

    // ================================================================================
    // #676: nvidia-smi throttle-reason/ECC ingestion - hidden entirely on non-NVIDIA systems.
    // Throttled to every few ticks rather than every 1s tick (a subprocess shell-out is a heavier
    // read than a perf-counter/registry read - same "let the tick fire often, do the heavier work
    // less often" shape EnergyThermalsViewModel's PowerPlanCheckInterval already establishes).
    // ================================================================================
    public bool NvidiaSmiAvailable { get; } = NvidiaSmiService.IsAvailable;
    public ObservableCollection<NvidiaSmiGpuStatus> NvidiaSmiStatuses { get; } = new();
    private static readonly TimeSpan NvidiaSmiCheckInterval = TimeSpan.FromSeconds(5);
    private DateTime _lastNvidiaSmiCheck = DateTime.MinValue;

    // ================================================================================
    // #677 (live half): per-tick "GPU Engine 3D counter flatlined while a foreground app is
    // running" detector - a pre-TDR hang signal that can be logged before the driver ever actually
    // resets. Persisted via GpuHangHistoryService so the Stability tab can show past sessions' hangs
    // too, not only this one.
    // ================================================================================
    public ObservableCollection<GpuHangEvent> HangEvents { get; } = new();
    private readonly Dictionary<string, (double Percent, int StreakTicks)> _engineFlatlineTracking = new();
    private const int FlatlineTicksThreshold = 5; // ~5s at this tab's 1s tick interval

    // Desktop-shell/system processes that legitimately own the foreground window without any user
    // app actually rendering through the GPU - excluded so an idle desktop never reads as a hang.
    private static readonly string[] ShellProcessNames =
    {
        "explorer", "dwm", "lockapp", "searchhost", "shellexperiencehost",
        "startmenuexperiencehost", "taskmanagerplus", "textinputhost",
    };

    // ================================================================================
    // #678: plain-English bottleneck verdict from the engine mix - 3D near 100% with low Copy/
    // Video is GPU-bound, low 3D with a saturated CPU core is CPU-bound, high Copy with VRAM at
    // capacity is transfer-bound. A pattern-match over data this tab already samples, not a
    // verified diagnosis - "quick flag, not a verdict", same tier as this app's other heuristics.
    // ================================================================================
    private string _bottleneckVerdictText = string.Empty;
    public string BottleneckVerdictText { get => _bottleneckVerdictText; private set => SetProperty(ref _bottleneckVerdictText, value); }

    private string _bottleneckEvidenceText = string.Empty;
    public string BottleneckEvidenceText { get => _bottleneckEvidenceText; private set => SetProperty(ref _bottleneckEvidenceText, value); }

    // ================================================================================
    // #679: per-engine history chart (3D/Copy/Video Decode/Video Encode/Compute) - the exact same
    // glow+core LineOf pattern every other history chart in this app uses, one pair per engine
    // type sharing one chart so an encode/decode spike (which explains a streaming stutter a single
    // aggregate "GPU busy" number hides completely) is visible right alongside 3D load.
    // ================================================================================
    private static readonly (string EngineType, SKColor Color)[] TrackedEngines =
    {
        ("3D", SKColors.DodgerBlue),
        ("Copy", SKColors.MediumPurple),
        ("Video Decode", SKColors.Gold),
        ("Video Encode", SKColors.OrangeRed),
        ("Compute", SKColors.MediumSeaGreen),
    };
    private readonly Dictionary<string, ObservableCollection<double>> _engineHistories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LineSeries<double>> _engineHistoryCoreSeries = new();
    public ISeries[] EngineHistorySeries { get; }
    public Axis[] EngineHistoryXAxes { get; }
    public Axis[] EngineHistoryYAxes { get; }

    // ================================================================================
    // #680: per-process GPU% split out by engine type (3D vs. Copy vs. Video Decode/Encode vs.
    // Compute) - set directly on the shared ProcessRow instances each tick, from
    // GpuMonitorService.LastPerProcessEngineUsage (see UpdateProcessEngineAttribution).
    // ================================================================================
    private readonly HashSet<int> _lastGpuActivePids = new();

    // ================================================================================
    // #681: "low clocks under load" flag - GPU core clock (from the sensor tree, same
    // EnergyThermalsViewModel poll the thermal-headroom tiles above already read) combined with
    // utilization. Low clocks at high utilization means power/thermal limiting; low clocks at low
    // utilization is normal idle - the very common "my GPU is stuck underclocked" misdiagnosis this
    // exists to head off. Self-calibrating against this session's own observed peak clock rather
    // than a hardcoded "should be" figure this app has no reliable source for per-model.
    // ================================================================================
    private double _sessionPeakGpuClockMHz;

    private double? _gpuCoreClockMHz;
    public double? GpuCoreClockMHz { get => _gpuCoreClockMHz; private set => SetProperty(ref _gpuCoreClockMHz, value); }

    private string _gpuClockStateText = string.Empty;
    public string GpuClockStateText { get => _gpuClockStateText; private set => SetProperty(ref _gpuClockStateText, value); }

    private bool _gpuClockStateIsWarning;
    public bool GpuClockStateIsWarning { get => _gpuClockStateIsWarning; private set => SetProperty(ref _gpuClockStateIsWarning, value); }

    // ================================================================================
    // #682/#683/#684: PCIe link speed/width per GPU/NVMe device (with per-boot drift detection via
    // PciLinkHistoryService), the active power plan's ASPM link-state setting, and eGPU/Thunderbolt
    // enclosure detection - all read together by one on-demand PowerShell shell-out
    // (PciLinkService), same "button + startup load" shape as the GPU-event-history card above, not
    // a per-tick poll (a real subprocess call).
    // ================================================================================
    public ObservableCollection<PciLinkInfo> PciLinks { get; } = new();
    public AsyncRelayCommand LoadPciLinkInfoCommand { get; }

    private string _pciLinkStatusText = "Not checked yet this session - click \"Check PCIe links\".";
    public string PciLinkStatusText { get => _pciLinkStatusText; private set => SetProperty(ref _pciLinkStatusText, value); }

    /// <summary>#684: the first Thunderbolt-attached GPU found (if any) - GpuView.xaml hides the
    /// dedicated eGPU card entirely when this is null, since most systems have no such device.</summary>
    public PciLinkInfo? EgpuLink => PciLinks.FirstOrDefault(l => l.Kind == "GPU" && l.IsThunderboltAttached);

    private int? _aspmAcIndex;
    private int? _aspmDcIndex;

    public string AspmStateText => _aspmAcIndex is null && _aspmDcIndex is null
        ? "Unknown"
        : $"AC: {AspmIndexText(_aspmAcIndex)}  ·  DC: {AspmIndexText(_aspmDcIndex)}";

    /// <summary>#683: true when either AC or DC is set to Moderate/Maximum power savings - a
    /// known cause of NVMe dropouts and eGPU/Thunderbolt disconnects (the link partner has to
    /// renegotiate out of a low-power link state before it can be used again).</summary>
    public bool AspmIsAggressive => _aspmAcIndex is >= 1 || _aspmDcIndex is >= 1;

    public AsyncRelayCommand SetAspmOffCommand { get; }

    private static string AspmIndexText(int? index) => index switch
    {
        0 => "Off",
        1 => "Moderate power savings",
        2 => "Maximum power savings",
        null => "Unknown",
        _ => $"{index} (unrecognized)",
    };

    public GpuViewModel(ProcessesViewModel processes, EnergyThermalsViewModel energyThermals, PerformanceViewModel performance)
    {
        _energyThermals = energyThermals;
        _processes = processes;
        _performance = performance;
        foreach (var a in _service.Adapters) InstalledAdapters.Add(a);

        var view = new CollectionViewSource { Source = processes.Processes }.View;
        if (view is ICollectionViewLiveShaping liveShaping && liveShaping.CanChangeLiveSorting)
        {
            liveShaping.LiveSortingProperties.Add(nameof(ProcessRow.GpuPercent));
            liveShaping.IsLiveSorting = true;
        }
        view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.GpuPercent), ListSortDirection.Descending));
        TopGpuProcesses = view;

        // #674: shared-VRAM ("spillover from system memory") trend chart - same glow+core LineOf
        // pattern every other history chart in this app follows.
        var spilloverColor = SKColors.Orchid;
        (_spilloverGlow, _spilloverCore) = LineOf(VramSpilloverHistory, spilloverColor);
        VramSpilloverSeries = new ISeries[] { _spilloverGlow, _spilloverCore };
        VramSpilloverXAxes = new[] { new Axis { IsVisible = false } };
        VramSpilloverYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => Formatting.FormatBytes(v),
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };

        // #679: per-engine history chart - one glow+core pair per tracked engine type, all sharing
        // one chart/legend so an encode/decode spike is visible right alongside 3D load.
        var engineSeriesList = new List<ISeries>();
        foreach (var (engineType, color) in TrackedEngines)
        {
            var history = NewHistory();
            _engineHistories[engineType] = history;
            var (glow, core) = LineOf(history, color);
            core.Name = engineType;
            _engineHistoryCoreSeries.Add(core);
            engineSeriesList.Add(glow);
            engineSeriesList.Add(core);
        }
        EngineHistorySeries = engineSeriesList.ToArray();
        EngineHistoryXAxes = new[] { new Axis { IsVisible = false } };
        EngineHistoryYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                MaxLimit = 100,
                Labeler = v => $"{v:0}%",
                LabelsPaint = new SolidColorPaint(AxisTextColor),
                SeparatorsPaint = new SolidColorPaint(AxisSeparatorColor) { StrokeThickness = 1 },
            },
        };

        LoadGpuEventHistoryCommand = new AsyncRelayCommand(_ => LoadGpuEventHistoryAsync());
        LoadPciLinkInfoCommand = new AsyncRelayCommand(_ => LoadPciLinkInfoAsync());
        SetAspmOffCommand = new AsyncRelayCommand(_ => SetAspmOffAsync());

        // #671/#672: cheap registry reads, safe to do eagerly (see class remarks).
        RefreshRegistryReadouts();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
        _ = LoadRefreshRateSummaryAsync();

        _ = LoadGpuEventHistoryAsync();
        // #682/#683/#684: a real PowerShell subprocess shell-out - on-demand only, loaded once at
        // startup (same shape as LoadGpuEventHistoryAsync above) plus the manual button.
        _ = LoadPciLinkInfoAsync();
    }

    /// <summary>#253 (nice-to-have) - see the field's own remarks.</summary>
    private async Task LoadRefreshRateSummaryAsync()
    {
        try
        {
            var audit = await Task.Run(DisplayModeService.ReadAudit);
            RefreshRateSummaryText = audit.Monitors.Count == 0
                ? string.Empty
                : string.Join("  •  ", audit.Monitors.Select(m => $"{m.MonitorName}: {m.CurrentRefreshHz} Hz (max {m.MaxRefreshHz} Hz)"))
                  + (audit.MixedRefreshRates ? " — mixed refresh rates across monitors." : string.Empty);
        }
        catch
        {
            // best-effort - the summary line just stays blank
        }
    }

    private void RefreshRegistryReadouts()
    {
        TdrRegistrySettings = GpuRegistryService.ReadTdrSettings();
        string? wddm = InstalledAdapters.FirstOrDefault()?.WddmVersion;
        HagsInfo = GpuRegistryService.ReadHagsInfo(wddm);
    }

    /// <summary>#670/#673/#677: the on-demand event-log + pnputil scan - TDR events, DXGI device-
    /// removed crashes, the unrecovered-reset count, and the driver-version correlation table all
    /// come from this one background pass.</summary>
    private async Task LoadGpuEventHistoryAsync()
    {
        GpuEventHistoryStatusText = "Scanning event logs and driver store (this can take a few seconds)...";
        try
        {
            var (summary, buckets) = await Task.Run(() =>
            {
                var resetSummary = _eventLog.ReadGpuResetSummary();
                var versionHistory = GpuRegistryService.ReadDisplayDriverVersionHistory();
                var crashEvents = _eventLog.ReadGpuDriverCrashEvents();
                var driverBuckets = BuildDriverVersionBuckets(versionHistory, resetSummary.TdrEvents, crashEvents);
                return (resetSummary, driverBuckets);
            });

            TdrEvents.Clear();
            foreach (var e in summary.TdrEvents) TdrEvents.Add(e);
            DeviceRemovedEvents.Clear();
            foreach (var e in summary.DeviceRemovedEvents) DeviceRemovedEvents.Add(e);
            UnrecoveredResetCount = summary.UnrecoveredResetCount;

            DriverVersionBuckets.Clear();
            foreach (var b in buckets) DriverVersionBuckets.Add(b);

            GpuEventHistoryStatusText = TdrEvents.Count == 0 && DeviceRemovedEvents.Count == 0
                ? "No GPU TDR or device-removed events found in the last 30 days."
                : string.Empty;

            RefreshRegistryReadouts(); // pick up any mid-session registry edit alongside a manual refresh
        }
        catch (Exception ex)
        {
            GpuEventHistoryStatusText = $"GPU event scan failed: {ex.Message}";
        }
    }

    /// <summary>#673: buckets TDR/crash timestamps by which driver-store package was the newest one
    /// published at that timestamp - see GpuDriverVersionBucket's remarks for exactly what
    /// "published at that timestamp" means (a best-effort join on package publish date, not a true
    /// point-in-time install log). Windows-newest-first; the newest package's window is open-ended
    /// ("now"), each older package's window ends where the next-newer one's begins.</summary>
    private static List<GpuDriverVersionBucket> BuildDriverVersionBuckets(
        List<(string Version, DateTime? PublishDate)> versionHistory,
        List<GpuTdrEvent> tdrEvents,
        List<StabilityEvent> crashEvents)
    {
        if (versionHistory.Count == 0) return new List<GpuDriverVersionBucket>();

        var ordered = versionHistory.OrderByDescending(v => v.PublishDate ?? DateTime.MinValue).ToList();
        var result = new List<GpuDriverVersionBucket>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var (version, publishDate) = ordered[i];
            DateTime windowStart = publishDate ?? DateTime.MinValue;
            DateTime windowEnd = i > 0 ? (ordered[i - 1].PublishDate ?? DateTime.MaxValue) : DateTime.MaxValue;

            result.Add(new GpuDriverVersionBucket
            {
                DriverVersion = version,
                PublishDate = publishDate,
                TdrCount = tdrEvents.Count(e => e.TimeCreated >= windowStart && e.TimeCreated < windowEnd),
                CrashCount = crashEvents.Count(e => e.TimeCreated >= windowStart && e.TimeCreated < windowEnd),
                IsCurrent = i == 0,
            });
        }
        return result;
    }

    /// <summary>#682/#683/#684: the on-demand PCIe link-state read - one PowerShell shell-out
    /// (PciLinkService, which itself folds in PciLinkHistoryService's per-boot drift comparison and
    /// #684's Thunderbolt-ancestor walk) plus the active plan's ASPM setting (powercfg).</summary>
    private async Task LoadPciLinkInfoAsync()
    {
        PciLinkStatusText = "Reading PCIe link state (shells out to PowerShell - can take a few seconds)...";
        try
        {
            var linksTask = PciLinkService.ReadAllAsync();
            var aspmTask = PowerPlanService.ReadAspmSettingAsync();
            await Task.WhenAll(linksTask, aspmTask);

            PciLinks.Clear();
            foreach (var l in linksTask.Result) PciLinks.Add(l);
            (_aspmAcIndex, _aspmDcIndex) = aspmTask.Result;
            OnPropertyChanged(nameof(AspmStateText));
            OnPropertyChanged(nameof(AspmIsAggressive));
            OnPropertyChanged(nameof(EgpuLink));

            PciLinkStatusText = PciLinks.Count == 0
                ? "No PCIe link data available - either PowerShell/the PnpDevice cmdlets aren't reachable, or no matching GPU/NVMe device was found."
                : string.Empty;
        }
        catch (Exception ex)
        {
            PciLinkStatusText = $"PCIe link check failed: {ex.Message}";
        }
    }

    /// <summary>#683: the "set to Off" one-click action - re-reads the setting afterward so the UI
    /// reflects what actually took effect rather than assuming success.</summary>
    private async Task SetAspmOffAsync()
    {
        try
        {
            var (success, error) = await PowerPlanService.SetAspmOffAsync();
            var (ac, dc) = await PowerPlanService.ReadAspmSettingAsync();
            _aspmAcIndex = ac;
            _aspmDcIndex = dc;
            OnPropertyChanged(nameof(AspmStateText));
            OnPropertyChanged(nameof(AspmIsAggressive));

            PciLinkStatusText = success
                ? "ASPM set to Off on the active power plan."
                : $"Couldn't set ASPM: {error ?? "unknown error"}";
        }
        catch (Exception ex)
        {
            PciLinkStatusText = $"Couldn't set ASPM: {ex.Message}";
        }
    }

    private async Task RefreshAsync()
    {
        if (!IsAvailable) return;
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var snapshot = await Task.Run(() => _service.Sample());
            LiveAdapters.Clear();
            foreach (var a in snapshot) LiveAdapters.Add(a);

            var primary = snapshot.FirstOrDefault(a => a.NameIsExact) ?? snapshot.FirstOrDefault();

            UpdateVramPressure(snapshot);
            DetectEngineFlatlines(snapshot);
            UpdateBottleneckVerdict(primary);
            UpdateEngineHistory(primary);
            UpdateProcessEngineAttribution();
            UpdateGpuClockState(primary);

            if (NvidiaSmiAvailable && DateTime.Now - _lastNvidiaSmiCheck >= NvidiaSmiCheckInterval)
            {
                _lastNvidiaSmiCheck = DateTime.Now;
                var statuses = await Task.Run(() => NvidiaSmiService.Query());
                NvidiaSmiStatuses.Clear();
                foreach (var s in statuses) NvidiaSmiStatuses.Add(s);
            }
        }
        catch
        {
            // Best-effort - a failed sample shouldn't crash the timer loop.
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    /// <summary>#674: charts the primary (best-identified, else first) adapter's shared-VRAM usage
    /// over time and derives a sustained-spillover verdict. See GpuAdapterSnapshot's remarks for
    /// exactly which perf counters back Dedicated/Shared/TotalCommitted here - "Committed", not the
    /// true DXGI-reported budget.</summary>
    private void UpdateVramPressure(List<GpuAdapterSnapshot> snapshot)
    {
        var primary = snapshot.FirstOrDefault(a => a.NameIsExact) ?? snapshot.FirstOrDefault();
        double sharedBytes = primary?.SharedVramUsedBytes ?? 0;
        PushHistory(VramSpilloverHistory, sharedBytes);

        if (primary is null)
        {
            VramPressureVerdictText = string.Empty;
            return;
        }

        bool overThreshold = sharedBytes > SpilloverThresholdBytes;
        _spilloverSustainWindow.Enqueue(overThreshold);
        while (_spilloverSustainWindow.Count > SpilloverSustainTicks) _spilloverSustainWindow.Dequeue();
        bool sustained = _spilloverSustainWindow.Count == SpilloverSustainTicks && _spilloverSustainWindow.All(v => v);

        if (sustained)
        {
            VramPressureVerdictText =
                $"Sustained shared-VRAM spillover on {primary.Name} - {Formatting.FormatBytes(sharedBytes)} has been paged into system memory over PCIe for the last {SpilloverSustainTicks}s straight. " +
                "This is the \"stutters but the GPU isn't at 100%\" shape: the driver is short on dedicated VRAM for the current workload, not short on compute (quick flag, not a verdict).";
        }
        else if (primary.DedicatedVramTotalBytes > 0 && primary.CommittedVsCapacityPercent > 100)
        {
            VramPressureVerdictText =
                $"Total committed adapter memory ({Formatting.FormatBytes(primary.TotalCommittedBytes)}) currently exceeds {primary.Name}'s installed VRAM capacity " +
                $"({Formatting.FormatBytes(primary.DedicatedVramTotalBytes)}) - some of it is being served from shared system memory right now.";
        }
        else
        {
            VramPressureVerdictText = string.Empty;
        }
    }

    /// <summary>#677 (live half): flags a "3D" engine utilization reading that hasn't changed across
    /// FlatlineTicksThreshold consecutive ticks while a real foreground app is running - a pre-TDR
    /// hang signal, logged once per contiguous flatline episode (not every tick past the
    /// threshold).</summary>
    private void DetectEngineFlatlines(List<GpuAdapterSnapshot> snapshot)
    {
        var liveLuids = new HashSet<string>(snapshot.Select(a => a.Luid));
        foreach (var stale in _engineFlatlineTracking.Keys.Where(k => !liveLuids.Contains(k)).ToList())
            _engineFlatlineTracking.Remove(stale);

        foreach (var adapter in snapshot)
        {
            var engine3D = adapter.Engines.FirstOrDefault(e => e.EngineType.Equals("3D", StringComparison.OrdinalIgnoreCase));
            if (engine3D is null)
            {
                _engineFlatlineTracking.Remove(adapter.Luid);
                continue;
            }

            bool unchanged = _engineFlatlineTracking.TryGetValue(adapter.Luid, out var state) &&
                              Math.Abs(state.Percent - engine3D.Percent) < 0.05;
            int streak = unchanged ? state.StreakTicks + 1 : 1;
            _engineFlatlineTracking[adapter.Luid] = (engine3D.Percent, streak);

            if (streak != FlatlineTicksThreshold || engine3D.Percent <= 0) continue;

            int fgPid = ForegroundProcessService.GetForegroundProcessId();
            var fgProcess = fgPid > 0 ? _processes.Processes.FirstOrDefault(p => p.Pid == fgPid) : null;
            if (fgProcess is null || ShellProcessNames.Contains(fgProcess.Name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                continue;

            var hang = new GpuHangEvent
            {
                DetectedAt = DateTime.Now,
                AdapterName = adapter.Name,
                StuckAtPercent = engine3D.Percent,
                DurationSeconds = FlatlineTicksThreshold * _timer.Interval.TotalSeconds,
                ForegroundProcessName = fgProcess.Name,
            };

            HangEvents.Insert(0, hang);
            while (HangEvents.Count > 20) HangEvents.RemoveAt(HangEvents.Count - 1);
            GpuHangHistoryService.Append(hang);
        }
    }

    /// <summary>#678: classifies the current tick's likely bottleneck from the engine mix plus
    /// (for the CPU-bound case) the shared PerformanceViewModel's per-core CPU data, and (for the
    /// transfer-bound case) #674's already-computed committed-vs-capacity VRAM figure. A pattern-
    /// match over data this tab already samples every tick - "quick flag, not a verdict", same as
    /// every other heuristic in this app.</summary>
    private void UpdateBottleneckVerdict(GpuAdapterSnapshot? primary)
    {
        if (primary is null || primary.Engines.Count == 0)
        {
            BottleneckVerdictText = string.Empty;
            BottleneckEvidenceText = string.Empty;
            return;
        }

        double Engine(string type) => primary.Engines.FirstOrDefault(e => e.EngineType.Equals(type, StringComparison.OrdinalIgnoreCase))?.Percent ?? 0;
        double engine3D = Engine("3D");
        double engineCopy = Engine("Copy");
        double engineDecode = Engine("Video Decode");
        double engineEncode = Engine("Video Encode");
        double maxCpuCorePercent = _performance.Cores.Count > 0 ? _performance.Cores.Max(c => c.Percent) : 0;

        if (engine3D < 5 && engineCopy < 5 && engineDecode < 5 && engineEncode < 5)
        {
            // Nothing meaningfully active - no bottleneck to report at all.
            BottleneckVerdictText = string.Empty;
            BottleneckEvidenceText = string.Empty;
        }
        else if (engine3D >= 90 && engineCopy < 30 && engineDecode < 30 && engineEncode < 30)
        {
            BottleneckVerdictText = "Likely bottleneck: GPU (3D)";
            BottleneckEvidenceText = $"3D engine at {engine3D:0}% with Copy/video engines idle - the GPU itself is the limiting factor (quick flag, not a verdict).";
        }
        else if (engine3D < 60 && maxCpuCorePercent >= 90)
        {
            BottleneckVerdictText = "Likely bottleneck: CPU";
            BottleneckEvidenceText = $"3D engine only at {engine3D:0}% while a CPU core is pegged at {maxCpuCorePercent:0}% - the GPU looks like it's waiting on the CPU to feed it work (quick flag, not a verdict).";
        }
        else if (engineCopy >= 50 && primary.CommittedVsCapacityPercent >= 95)
        {
            BottleneckVerdictText = "Likely bottleneck: transfer (Copy engine / VRAM budget)";
            BottleneckEvidenceText = $"Copy engine at {engineCopy:0}% with committed VRAM at {primary.CommittedVsCapacityPercent:0}% of capacity - data looks like it's being paged over PCIe rather than compute-bound (quick flag, not a verdict).";
        }
        else if (engineDecode >= 60 || engineEncode >= 60)
        {
            string which = engineDecode >= engineEncode ? "video decode" : "video encode";
            double pct = Math.Max(engineDecode, engineEncode);
            BottleneckVerdictText = $"Likely bottleneck: GPU ({which})";
            BottleneckEvidenceText = $"The {which} engine is at {pct:0}% while 3D is only at {engine3D:0}% - a fixed-function media engine, not the shader cores, is the limiting factor here.";
        }
        else
        {
            BottleneckVerdictText = "No single engine looks clearly saturated right now";
            BottleneckEvidenceText = $"3D {engine3D:0}%  ·  Copy {engineCopy:0}%  ·  Decode {engineDecode:0}%  ·  Encode {engineEncode:0}%";
        }
    }

    /// <summary>#679: pushes this tick's per-engine percentages (3D/Copy/Video Decode/Video Encode/
    /// Compute) onto their own history series - an engine this adapter didn't report at all this
    /// tick (or no primary adapter at all) pushes 0, same "flat 0 rather than a gap" convention
    /// every other history chart in this app already follows.</summary>
    private void UpdateEngineHistory(GpuAdapterSnapshot? primary)
    {
        foreach (var (engineType, history) in _engineHistories)
        {
            double value = primary?.Engines.FirstOrDefault(e => e.EngineType.Equals(engineType, StringComparison.OrdinalIgnoreCase))?.Percent ?? 0;
            PushHistory(history, value);
        }
    }

    /// <summary>#680: writes this tick's per-engine breakdown onto the shared ProcessRow instances
    /// (GpuMonitorService.LastPerProcessEngineUsage, from the same pid_..._engtype_... instance-name
    /// parse the per-adapter breakdown already uses) - a pid that stopped using the GPU since the
    /// last tick gets cleared rather than left showing a stale engine label.</summary>
    private void UpdateProcessEngineAttribution()
    {
        var byPid = _service.LastPerProcessEngineUsage;
        var currentPids = new HashSet<int>(byPid.Keys);

        foreach (var stalePid in _lastGpuActivePids)
        {
            if (currentPids.Contains(stalePid)) continue;
            var row = _processes.Processes.FirstOrDefault(p => p.Pid == stalePid);
            if (row is null) continue;
            row.TopGpuEngine = string.Empty;
            row.TopGpuEnginePercent = 0;
        }

        foreach (var (pid, engines) in byPid)
        {
            if (engines.Count == 0) continue;
            var row = _processes.Processes.FirstOrDefault(p => p.Pid == pid);
            if (row is null) continue;
            var top = engines[0];
            row.TopGpuEngine = top.EngineType;
            row.TopGpuEnginePercent = top.Percent;
        }

        _lastGpuActivePids.Clear();
        foreach (var pid in currentPids) _lastGpuActivePids.Add(pid);
    }

    /// <summary>#681: combines the primary adapter's utilization with its GPU core clock (read from
    /// the sensor tree EnergyThermalsViewModel already polls) to flag power/thermal limiting vs.
    /// normal idle - see the class remarks for why this self-calibrates against this session's own
    /// observed peak clock rather than a per-model "should be" figure this app has no source for.</summary>
    private void UpdateGpuClockState(GpuAdapterSnapshot? primary)
    {
        var coreClockReading = _energyThermals.Clocks.FirstOrDefault(r =>
            (r.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel) &&
            r.SensorName.Contains("Core", StringComparison.OrdinalIgnoreCase));

        GpuCoreClockMHz = coreClockReading?.Value;

        if (GpuCoreClockMHz is not { } clock || primary is null)
        {
            GpuClockStateText = string.Empty;
            GpuClockStateIsWarning = false;
            return;
        }

        if (clock > _sessionPeakGpuClockMHz) _sessionPeakGpuClockMHz = clock;
        double utilization = primary.TotalUtilizationPercent;

        if (_sessionPeakGpuClockMHz < 200)
        {
            // Not enough of a peak observed yet this session to judge "low" against - stay silent
            // rather than flag against a peak that hasn't actually been reached yet.
            GpuClockStateText = string.Empty;
            GpuClockStateIsWarning = false;
        }
        else if (utilization >= 60 && clock < _sessionPeakGpuClockMHz * 0.55)
        {
            GpuClockStateText = $"Low clocks under load - {clock:0} MHz at {utilization:0}% utilization, well below this session's observed peak of {_sessionPeakGpuClockMHz:0} MHz. Looks like power or thermal limiting, not a stuck/underclocked GPU (quick flag, not a verdict).";
            GpuClockStateIsWarning = true;
        }
        else if (utilization < 15)
        {
            GpuClockStateText = $"Idle - {clock:0} MHz at {utilization:0}% utilization is expected low-power behavior, not a fault.";
            GpuClockStateIsWarning = false;
        }
        else
        {
            GpuClockStateText = $"Normal - {clock:0} MHz at {utilization:0}% utilization.";
            GpuClockStateIsWarning = false;
        }
    }

    // ================================================================================
    // Chart plumbing - same shapes PerformanceViewModel/EnergyThermalsViewModel already establish
    // for every other history chart in this app.
    // ================================================================================
    private static readonly SKColor AxisTextColor = new(0x9A, 0x9A, 0xA2);
    private static readonly SKColor AxisSeparatorColor = new(0x33, 0x33, 0x3A, 160);

    private static ObservableCollection<double> NewHistory()
    {
        var col = new ObservableCollection<double>();
        for (int i = 0; i < HistoryLength; i++) col.Add(0);
        return col;
    }

    private static void PushHistory(ObservableCollection<double> history, double value)
    {
        history.Add(value);
        if (history.Count > HistoryLength) history.RemoveAt(0);
    }

    private const float CoreStrokeWidth = 2f;
    private const float GlowStrokeWidth = 7f;

    private static (LineSeries<double> Glow, LineSeries<double> Core) LineOf(ObservableCollection<double> values, SKColor color)
    {
        var glow = new LineSeries<double>
        {
            Values = values,
            Stroke = new SolidColorPaint(color.WithAlpha(70), GlowStrokeWidth),
            Fill = null,
            GeometryStroke = null,
            GeometryFill = null,
            LineSmoothness = 0.3,
            IsHoverable = false,
            IsVisibleAtLegend = false,
        };
        var core = new LineSeries<double>
        {
            Values = values,
            Stroke = new SolidColorPaint(color, CoreStrokeWidth),
            Fill = new LinearGradientPaint(color.WithAlpha(90), color.WithAlpha(0), new SKPoint(0, 0), new SKPoint(0, 1)),
            GeometryStroke = null,
            GeometryFill = null,
            LineSmoothness = 0.3,
        };
        return (glow, core);
    }

    /// <summary>Repaints chart axis text/gridlines to match the active theme family - see
    /// PerformanceViewModel.ApplyAxisTheme's remarks; same SkiaSharp-outside-WPF-resources gap.</summary>
    public void ApplyAxisTheme(Color text, Color separator)
    {
        var textSk = new SKColor(text.R, text.G, text.B);
        var sepSk = new SKColor(separator.R, separator.G, separator.B, separator.A);
        VramSpilloverYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        VramSpilloverYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
        EngineHistoryYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        EngineHistoryYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
    }

    public void Dispose()
    {
        _timer.Stop();
        _service.Dispose();
    }
}
