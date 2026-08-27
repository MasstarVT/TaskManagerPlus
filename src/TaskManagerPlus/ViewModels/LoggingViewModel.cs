using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Win32;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Backs the global "Start/Stop Logging" control in the footer status bar - a CSV log of every
/// metric the app currently displays (CPU total + per-core, memory breakdown, disk, network, and
/// every Energy &amp; Thermals sensor reading), one row per second, the same "log everything to
/// one CSV" approach HWiNFO's own logging feature uses.
///
/// Deliberately reads already-polled state off PerformanceViewModel/EnergyThermalsViewModel
/// rather than sampling hardware itself - logging shouldn't add a second, redundant poller on
/// top of the ones those two already run.
/// </summary>
public sealed class LoggingViewModel : ObservableObject, IDisposable
{
    private readonly LoggingService _logging = new();
    private readonly PerformanceViewModel _performance;
    private readonly EnergyThermalsViewModel _energyThermals;
    private readonly DispatcherTimer _timer;

    // Column set is fixed for the lifetime of one logging session, snapshotted when Start is
    // clicked - if the sensor list or core count ever changed mid-session the CSV's columns
    // would no longer line up, so later ticks just leave a cell blank for anything that no
    // longer matches rather than changing the header.
    private int _coreCountAtStart;
    private List<(string Identifier, SensorType Type)> _sensorColumnsAtStart = new();

    // #95: auto-start rolling buffer - a separate, independent "always logging the last N
    // minutes to memory" mode that runs whenever manual logging (above) isn't active. Its own
    // column snapshot (taken when the buffer starts, same reasoning as the manual snapshot
    // above) and its own fixed-size queue, periodically flushed to one fixed file on disk so a
    // crash mid-session still leaves a usable file behind, not just an in-memory buffer that
    // dies with the process.
    private readonly LoggingSettings _loggingSettings = LoggingSettingsService.Load();
    private readonly Queue<string> _rollingBuffer = new();
    private int _rollingCoreCountAtStart;
    private List<(string Identifier, SensorType Type)> _rollingSensorColumnsAtStart = new();
    private int _rollingFlushCountdown;
    private static string RollingBufferPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskManagerPlus", "Logs", "rolling-buffer.csv");

    public bool AutoStartRollingBufferEnabled
    {
        get => _loggingSettings.AutoStartRollingBuffer;
        set
        {
            if (_loggingSettings.AutoStartRollingBuffer == value) return;
            _loggingSettings.AutoStartRollingBuffer = value;
            LoggingSettingsService.Save(_loggingSettings);
            OnPropertyChanged();
            if (value) StartRollingBuffer(); else _rollingBuffer.Clear();
        }
    }

    /// <summary>Round 11, #76: how many seconds elapse between rows, for both manual logging and
    /// the rolling buffer - the original app always wrote one row per second; a longer interval
    /// trades resolution for a smaller file/less disk I/O on a long unattended session. Only
    /// 1/5/10s are offered (matching the assignment's guidance) rather than an arbitrary value.
    /// Changing it live re-intervals the already-running timer immediately.</summary>
    public int SampleIntervalSeconds
    {
        get => _loggingSettings.SampleIntervalSeconds;
        set
        {
            value = value is 1 or 5 or 10 ? value : 1;
            if (_loggingSettings.SampleIntervalSeconds == value) return;
            _loggingSettings.SampleIntervalSeconds = value;
            LoggingSettingsService.Save(_loggingSettings);
            OnPropertyChanged();
            _timer.Interval = TimeSpan.FromSeconds(value);
        }
    }

    public IReadOnlyList<int> SampleIntervalOptions { get; } = new[] { 1, 5, 10 };

    /// <summary>Round 11, #77: whether old rotated log parts get swept automatically - see
    /// LoggingService.CleanupOldRotatedParts.</summary>
    public bool AutoCleanupEnabled
    {
        get => _loggingSettings.AutoCleanupEnabled;
        set
        {
            if (_loggingSettings.AutoCleanupEnabled == value) return;
            _loggingSettings.AutoCleanupEnabled = value;
            LoggingSettingsService.Save(_loggingSettings);
            OnPropertyChanged();
        }
    }

    public int AutoCleanupDays
    {
        get => _loggingSettings.AutoCleanupDays;
        set
        {
            value = Math.Max(1, value);
            if (_loggingSettings.AutoCleanupDays == value) return;
            _loggingSettings.AutoCleanupDays = value;
            LoggingSettingsService.Save(_loggingSettings);
            OnPropertyChanged();
        }
    }

    public bool IsLogging => _logging.IsLogging;
    public string? LogFilePath => _logging.FilePath;
    public string LogFileName => LogFilePath is null ? string.Empty : Path.GetFileName(LogFilePath);

    public RelayCommand ToggleLoggingCommand { get; }

    // #75: event markers - lets the user tag "this is when it happened" while reproducing an
    // issue, without needing to cross-reference a separate stopwatch/timestamp against the CSV
    // afterward. Written as an always-present trailing "Marker" column (blank on every other
    // row) rather than a separate line, so the CSV's column count never changes mid-file.
    private string _markerText = string.Empty;
    public string MarkerText { get => _markerText; set => SetProperty(ref _markerText, value); }
    private string? _pendingMarker;

    public RelayCommand AddMarkerCommand { get; }

    // #96: log file viewer/replay - loads a previously recorded CSV (this app's own, from either
    // manual logging or the rolling buffer above) and re-charts its headline figures, so a past
    // session can be inspected without an external tool like Excel. See LogReplayService's
    // remarks for why only a handful of well-known columns are pulled out by name.
    public RelayCommand LoadLogFileCommand { get; }

    private ISeries[]? _replaySeries;
    public ISeries[]? ReplaySeries { get => _replaySeries; private set => SetProperty(ref _replaySeries, value); }
    public Axis[] ReplayXAxes { get; }
    public Axis[] ReplayYAxes { get; }

    private string _replayStatusText = string.Empty;
    public string ReplayStatusText { get => _replayStatusText; private set => SetProperty(ref _replayStatusText, value); }

    public LoggingViewModel(PerformanceViewModel performance, EnergyThermalsViewModel energyThermals)
    {
        _performance = performance;
        _energyThermals = energyThermals;

        ToggleLoggingCommand = new RelayCommand(_ => ToggleLogging());
        AddMarkerCommand = new RelayCommand(_ => AddMarker(), _ => IsLogging);
        LoadLogFileCommand = new RelayCommand(_ => LoadLogFile());

        ReplayXAxes = new[] { new Axis { Labels = Array.Empty<string>(), LabelsPaint = new SolidColorPaint(new SKColor(0x9A, 0x9A, 0xA2)), SeparatorsPaint = null } };
        ReplayYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0, MaxLimit = 100, Labeler = v => $"{v:0}%",
                LabelsPaint = new SolidColorPaint(new SKColor(0x9A, 0x9A, 0xA2)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(0x33, 0x33, 0x3A, 160)) { StrokeThickness = 1 },
            },
        };

        _logging.Rotated += () => RaiseLoggingChanged();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(_loggingSettings.SampleIntervalSeconds) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        if (AutoStartRollingBufferEnabled) StartRollingBuffer();

        // #77: sweep old rotated parts once per launch, not on a timer - a logs folder only grows
        // between app runs, so there's nothing to gain from repeating this check every session tick.
        if (_loggingSettings.AutoCleanupEnabled)
        {
            var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskManagerPlus", "Logs");
            Task.Run(() => LoggingService.CleanupOldRotatedParts(logsDir, _loggingSettings.AutoCleanupDays, activeFilePath: null));
        }
    }

    private void Tick()
    {
        if (IsLogging) { WriteRowIfLogging(); return; }
        if (AutoStartRollingBufferEnabled) WriteRollingBufferRow();
    }

    private void StartRollingBuffer()
    {
        _rollingCoreCountAtStart = _performance.Cores.Count;
        _rollingSensorColumnsAtStart = _energyThermals.Temperatures
            .Concat(_energyThermals.Fans).Concat(_energyThermals.Voltages).Concat(_energyThermals.Wattages)
            .Select(s => (s.Identifier, s.Type)).ToList();
        _rollingBuffer.Clear();
        _rollingFlushCountdown = 0;
    }

    /// <summary>Builds one row the same shape WriteRowIfLogging does, using the rolling buffer's
    /// own column snapshot, then trims to the configured window (minutes * 1 row/sec) and flushes
    /// to the fixed rolling-buffer file every 10s - frequent enough that a crash leaves a
    /// reasonably fresh file, infrequent enough not to be wasteful disk I/O for a background,
    /// always-on feature.</summary>
    private void WriteRollingBufferRow()
    {
        var row = BuildRow(_rollingCoreCountAtStart, _rollingSensorColumnsAtStart);
        _rollingBuffer.Enqueue(string.Join(",", row.Select(Escape)));

        // #76: one row is written per SampleIntervalSeconds, not necessarily per second - divide
        // through so "N minutes" of buffer still means N minutes of wall-clock time either way.
        int maxRows = Math.Max(1, _loggingSettings.RollingBufferMinutes * 60 / Math.Max(1, _loggingSettings.SampleIntervalSeconds));
        while (_rollingBuffer.Count > maxRows) _rollingBuffer.Dequeue();

        if (++_rollingFlushCountdown < 10) return;
        _rollingFlushCountdown = 0;

        try
        {
            var dir = Path.GetDirectoryName(RollingBufferPath)!;
            Directory.CreateDirectory(dir);
            var lines = new List<string> { string.Join(",", BuildHeaders(_rollingCoreCountAtStart, _rollingSensorColumnsAtStart).Select(Escape)) };
            lines.AddRange(_rollingBuffer);
            File.WriteAllLines(RollingBufferPath, lines);
        }
        catch
        {
            // Best-effort - a failed flush just means the on-disk copy is a bit stale; the
            // in-memory buffer itself is unaffected.
        }
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public void ApplyAxisTheme(System.Windows.Media.Color text, System.Windows.Media.Color separator)
    {
        var textSk = new SKColor(text.R, text.G, text.B);
        var sepSk = new SKColor(separator.R, separator.G, separator.B, separator.A);
        ReplayXAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        ReplayYAxes[0].LabelsPaint = new SolidColorPaint(textSk);
        ReplayYAxes[0].SeparatorsPaint = new SolidColorPaint(sepSk) { StrokeThickness = 1 };
    }

    private void LoadLogFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load a log file to replay",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskManagerPlus", "Logs"),
        };
        if (dialog.ShowDialog() != true) return;

        var (result, error) = LogReplayService.Parse(dialog.FileName);
        if (result is null)
        {
            ReplaySeries = null;
            ReplayStatusText = error ?? "Couldn't read that file.";
            return;
        }

        var cpuColor = SKColors.DeepSkyBlue;
        var ramColor = SKColors.MediumPurple;
        var diskColor = SKColors.Orange;
        ISeries LineOf(List<double> values, SKColor color, string name) => new LineSeries<double>
        {
            Values = values, Name = name, Stroke = new SolidColorPaint(color, 2f), Fill = null,
            GeometryStroke = null, GeometryFill = null, LineSmoothness = 0.2,
        };
        ReplaySeries = new[]
        {
            LineOf(result.CpuPercent, cpuColor, "CPU %"),
            LineOf(result.RamPercent, ramColor, "RAM %"),
            LineOf(result.DiskPercent, diskColor, "Disk %"),
        };

        int n = result.Timestamps.Count;
        int labelEvery = Math.Max(1, n / 8);
        ReplayXAxes[0].Labels = result.Timestamps
            .Select((t, i) => i % labelEvery == 0 ? t.ToString("t") : string.Empty)
            .ToArray();

        ReplayStatusText = $"{result.RowCount} rows: {result.Timestamps[0]:g} – {result.Timestamps[^1]:g} ({Path.GetFileName(dialog.FileName)})";
    }

    private void AddMarker()
    {
        _pendingMarker = string.IsNullOrWhiteSpace(MarkerText) ? "Marker" : MarkerText.Trim();
        MarkerText = string.Empty;
    }

    private void ToggleLogging()
    {
        if (IsLogging)
        {
            _logging.Stop();
            RaiseLoggingChanged();
            return;
        }

        var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskManagerPlus", "Logs");
        try { Directory.CreateDirectory(logsDir); } catch { /* SaveFileDialog still works without a pre-created folder */ }

        var dialog = new SaveFileDialog
        {
            Title = "Start logging to file",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = $"TaskManagerPlus-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv",
            InitialDirectory = logsDir,
        };
        if (dialog.ShowDialog() != true) return;

        StartLogging(dialog.FileName);
    }

    private void StartLogging(string path)
    {
        _coreCountAtStart = _performance.Cores.Count;
        _sensorColumnsAtStart = _energyThermals.Temperatures
            .Concat(_energyThermals.Fans)
            .Concat(_energyThermals.Voltages)
            .Concat(_energyThermals.Wattages)
            .Select(s => (s.Identifier, s.Type))
            .ToList();

        _logging.Start(path, BuildHeaders(_coreCountAtStart, _sensorColumnsAtStart));
        WriteRowIfLogging(); // capture the current values immediately, not just on the next tick
        RaiseLoggingChanged();
    }

    private List<string> BuildHeaders(int coreCount, List<(string Identifier, SensorType Type)> sensorColumns)
    {
        var headers = new List<string>
        {
            "Timestamp",
            "CPU Total (%)", "CPU Clock (GHz)",
        };
        for (int i = 0; i < coreCount; i++)
            headers.Add($"CPU Core {i} (%)");

        headers.AddRange(new[]
        {
            // CPU diagnostics (#13/#14/#15) - keep the CSV consistent with what the CPU tab shows.
            "CPU Interrupt (%)", "CPU DPC (%)", "Context Switches (/s)", "CPU Queue Length",
            "RAM Used (GB)", "RAM Total (GB)", "RAM (%)", "RAM Available (GB)",
            "Committed (GB)", "Commit Limit (GB)", "Cached (GB)",
            // Memory diagnostics (#20/#22/#24).
            "Page Faults Hard (/s)", "Page Faults Soft (/s)", "Standby (GB)", "Nonpaged Pool (GB)", "Paged Pool (GB)",
            "Disk Active (%)", "Disk Read (B/s)", "Disk Write (B/s)",
            "Network Receive (B/s)", "Network Send (B/s)",
            // Network diagnostic (#32).
            "TCP Retransmits (/s)",
        });

        var allSensors = _energyThermals.Temperatures
            .Concat(_energyThermals.Fans)
            .Concat(_energyThermals.Voltages)
            .Concat(_energyThermals.Wattages)
            .ToDictionary(s => s.Identifier);
        foreach (var (identifier, type) in sensorColumns)
        {
            var name = allSensors.TryGetValue(identifier, out var s)
                ? $"{s.HardwareName} {s.SensorName}"
                : identifier;
            headers.Add($"{name} ({UnitOf(type)})");
        }

        // #75: always present, last column - blank on every row except one where AddMarkerCommand
        // was used, so the CSV's column set stays fixed for the file's lifetime either way.
        headers.Add("Marker");

        return headers;
    }

    private void WriteRowIfLogging()
    {
        if (!IsLogging) return;
        _logging.WriteRow(BuildRow(_coreCountAtStart, _sensorColumnsAtStart));
    }

    private List<string> BuildRow(int coreCount, List<(string Identifier, SensorType Type)> sensorColumns)
    {
        var row = new List<string> { DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) };
        row.Add(Num(_performance.CpuCurrentPercent));
        row.Add(Num(_performance.CpuCurrentClockGhz));

        for (int i = 0; i < coreCount; i++)
            row.Add(i < _performance.Cores.Count ? Num(_performance.Cores[i].Percent) : string.Empty);

        row.Add(Num(_performance.CpuInterruptPercent));
        row.Add(Num(_performance.CpuDpcPercent));
        row.Add(Num(_performance.ContextSwitchesPerSec));
        row.Add(Num(_performance.CpuQueueLength));

        row.Add(Num(_performance.RamUsedGb));
        row.Add(Num(_performance.RamTotalGb));
        row.Add(Num(_performance.RamPercent));
        row.Add(Num(_performance.RamAvailableGb));
        row.Add(Num(_performance.CommittedGb));
        row.Add(Num(_performance.CommitLimitGb));
        row.Add(Num(_performance.CachedGb));

        row.Add(Num(_performance.HardFaultsPerSec));
        row.Add(Num(_performance.SoftFaultsPerSec));
        row.Add(Num(_performance.StandbyGb));
        row.Add(Num(_performance.PoolNonpagedGb));
        row.Add(Num(_performance.PoolPagedGb));

        row.Add(Num(_performance.DiskPercent));
        row.Add(Num(_performance.DiskReadBps));
        row.Add(Num(_performance.DiskWriteBps));
        row.Add(Num(_performance.NetworkReceiveBps));
        row.Add(Num(_performance.NetworkSendBps));
        row.Add(Num(_performance.TcpRetransmitsPerSec));

        var allSensors = _energyThermals.Temperatures
            .Concat(_energyThermals.Fans)
            .Concat(_energyThermals.Voltages)
            .Concat(_energyThermals.Wattages)
            .ToDictionary(s => s.Identifier);
        foreach (var (identifier, _) in sensorColumns)
        {
            row.Add(allSensors.TryGetValue(identifier, out var s) && s.Value.HasValue
                ? Num(s.Value.Value)
                : string.Empty);
        }

        row.Add(_pendingMarker ?? string.Empty);
        _pendingMarker = null;

        return row;
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string UnitOf(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Fan => "RPM",
        SensorType.Voltage => "V",
        SensorType.Power => "W",
        _ => string.Empty,
    };

    private void RaiseLoggingChanged()
    {
        OnPropertyChanged(nameof(IsLogging));
        OnPropertyChanged(nameof(LogFilePath));
        OnPropertyChanged(nameof(LogFileName));
    }

    public void Dispose()
    {
        _timer.Stop();
        _logging.Dispose();
    }
}
