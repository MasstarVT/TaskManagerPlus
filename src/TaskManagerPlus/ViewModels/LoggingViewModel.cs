using System.Globalization;
using System.IO;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using TaskManagerPlus.Common;
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

    public bool IsLogging => _logging.IsLogging;
    public string? LogFilePath => _logging.FilePath;
    public string LogFileName => LogFilePath is null ? string.Empty : Path.GetFileName(LogFilePath);

    public RelayCommand ToggleLoggingCommand { get; }

    public LoggingViewModel(PerformanceViewModel performance, EnergyThermalsViewModel energyThermals)
    {
        _performance = performance;
        _energyThermals = energyThermals;

        ToggleLoggingCommand = new RelayCommand(_ => ToggleLogging());

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => WriteRowIfLogging();
        _timer.Start();
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

        _logging.Start(path, BuildHeaders());
        WriteRowIfLogging(); // capture the current values immediately, not just on the next tick
        RaiseLoggingChanged();
    }

    private List<string> BuildHeaders()
    {
        var headers = new List<string>
        {
            "Timestamp",
            "CPU Total (%)", "CPU Clock (GHz)",
        };
        for (int i = 0; i < _coreCountAtStart; i++)
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
        foreach (var (identifier, type) in _sensorColumnsAtStart)
        {
            var name = allSensors.TryGetValue(identifier, out var s)
                ? $"{s.HardwareName} {s.SensorName}"
                : identifier;
            headers.Add($"{name} ({UnitOf(type)})");
        }

        return headers;
    }

    private void WriteRowIfLogging()
    {
        if (!IsLogging) return;

        var row = new List<string> { DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) };
        row.Add(Num(_performance.CpuCurrentPercent));
        row.Add(Num(_performance.CpuCurrentClockGhz));

        for (int i = 0; i < _coreCountAtStart; i++)
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
        foreach (var (identifier, _) in _sensorColumnsAtStart)
        {
            row.Add(allSensors.TryGetValue(identifier, out var s) && s.Value.HasValue
                ? Num(s.Value.Value)
                : string.Empty);
        }

        _logging.WriteRow(row);
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
