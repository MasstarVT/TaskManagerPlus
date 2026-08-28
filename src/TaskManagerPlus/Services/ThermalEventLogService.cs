using System.IO;
using System.Text.Json;
using TaskManagerPlus.Models;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Services;

/// <summary>
/// #943: edge-triggered thermal/throttle event logger - appends one line to
/// AppPaths.SettingsDirectory\thermal-events.jsonl the *moment* CpuViewModel's IsThrottling/
/// IsPowerLimited flags flip, or EnergyThermalsViewModel's CPU package temperature crosses the
/// user's configured alert threshold (AlertThresholds.TempC). Wired via <see cref="Attach"/> from
/// MainViewModel's constructor - a PropertyChanged subscription on view-models that already tick
/// on their own timers regardless, not a new poll timer of its own, per CLAUDE.md's "on-demand vs.
/// polled" convention and this task's own instructions.
///
/// Append-only by design: a log line is never rewritten or deleted, so a fresh install with no
/// throttle/threshold history yet simply has no file - <see cref="ReadAll"/> degrades to an empty
/// list, not a fabricated "no events" placeholder (CLAUDE.md's "degrade to Unknown/0/hidden - never
/// fabricate").
/// </summary>
public sealed class ThermalEventLogService
{
    private static string LogPath => AppPaths.GetPath("thermal-events.jsonl");

    private bool? _lastThrottling;
    private bool? _lastPowerLimited;
    private bool? _lastOverThreshold;
    private AlertThresholds _cachedThresholds = AlertThresholdsService.Load();
    private DateTime _thresholdsCachedAtUtc = DateTime.MinValue;

    /// <summary>Subscribes to both view-models' PropertyChanged - call exactly once, after both
    /// are constructed (MainViewModel already builds EnergyThermals before Cpu, and both before
    /// Troubleshoot - see MainViewModel's constructor remarks).</summary>
    public void Attach(CpuViewModel cpu, EnergyThermalsViewModel energyThermals)
    {
        cpu.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CpuViewModel.IsThrottling))
                CheckBoolTransition(ref _lastThrottling, cpu.IsThrottling, "Thermal throttling", cpu.ThrottleText);
            else if (e.PropertyName == nameof(CpuViewModel.IsPowerLimited))
                CheckBoolTransition(ref _lastPowerLimited, cpu.IsPowerLimited, "Power limit", cpu.PowerLimitText);
        };

        energyThermals.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(EnergyThermalsViewModel.CpuPackageTempC)) return;
            var thresholds = RefreshThresholdsIfStale();
            if (!thresholds.TempEnabled) return;
            if (energyThermals.CpuPackageTempC is not { } temp) return;

            bool over = temp >= thresholds.TempC;
            bool isFirstObservation = _lastOverThreshold is null;
            if (_lastOverThreshold == over) return;
            _lastOverThreshold = over;
            if (isFirstObservation) return; // baseline read on attach, not a real transition

            Append(over ? "Temperature alert threshold crossed (above)" : "Temperature alert threshold crossed (below)",
                $"CPU package temperature {temp:0.#}°C vs. the configured {thresholds.TempC:0}°C alert threshold.");
        };
    }

    private AlertThresholds RefreshThresholdsIfStale()
    {
        // Reload at most once every 30s rather than on every EnergyThermals tick (which fires far
        // more often than the threshold setting itself ever changes) - the same debounce shape
        // EnergyThermalsViewModel._lastThrottleLogged already uses for its own 30s-cooldown log.
        if ((DateTime.UtcNow - _thresholdsCachedAtUtc).TotalSeconds >= 30)
        {
            _cachedThresholds = AlertThresholdsService.Load();
            _thresholdsCachedAtUtc = DateTime.UtcNow;
        }
        return _cachedThresholds;
    }

    private void CheckBoolTransition(ref bool? last, bool current, string kind, string detailText)
    {
        bool isFirstObservation = last is null;
        if (last == current) return;
        last = current;
        if (isFirstObservation) return; // baseline read on attach, not a real transition

        Append($"{kind} {(current ? "started" : "cleared")}", detailText);
    }

    private static void Append(string title, string detail)
    {
        try
        {
            var entry = new ThermalEventLogEntry { Timestamp = DateTime.Now, Title = title, Detail = detail };
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch
        {
            // Best-effort - a failed append shouldn't affect live throttle detection elsewhere.
        }
    }

    /// <summary>Reads every logged transition back as Timeline events for the Thermal events lane -
    /// safe to call often (a plain sequential file read), unlike the WMI/event-log/registry
    /// sources TimelineService reads for the other lanes.</summary>
    public static List<TimelineEvent> ReadAll()
    {
        var events = new List<TimelineEvent>();
        try
        {
            if (!File.Exists(LogPath)) return events;
            foreach (var line in File.ReadAllLines(LogPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<ThermalEventLogEntry>(line);
                    if (entry is null) continue;
                    events.Add(new TimelineEvent
                    {
                        Lane = TimelineLane.ThermalEvents,
                        Timestamp = entry.Timestamp,
                        Title = entry.Title,
                        Detail = entry.Detail,
                        Source = "thermal-events.jsonl",
                        IsFailure = entry.Title.Contains("started", StringComparison.OrdinalIgnoreCase) ||
                                    entry.Title.Contains("(above)", StringComparison.OrdinalIgnoreCase),
                    });
                }
                catch { /* one malformed line shouldn't stop the rest of the read */ }
            }
        }
        catch
        {
            // File missing/locked - degrade to "no thermal events recorded yet".
        }
        return events;
    }
}

/// <summary>One line of thermal-events.jsonl - see ThermalEventLogService's remarks.</summary>
internal sealed class ThermalEventLogEntry
{
    public DateTime Timestamp { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}
