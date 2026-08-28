using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
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

    public GpuViewModel(ProcessesViewModel processes, EnergyThermalsViewModel energyThermals)
    {
        _energyThermals = energyThermals;
        _processes = processes;
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

        LoadGpuEventHistoryCommand = new AsyncRelayCommand(_ => LoadGpuEventHistoryAsync());

        // #671/#672: cheap registry reads, safe to do eagerly (see class remarks).
        RefreshRegistryReadouts();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();

        _ = LoadGpuEventHistoryAsync();
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

            UpdateVramPressure(snapshot);
            DetectEngineFlatlines(snapshot);

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
    }

    public void Dispose()
    {
        _timer.Stop();
        _service.Dispose();
    }
}
