namespace TaskManagerPlus.Models;

/// <summary>
/// #296-300: the flight recorder - a fixed-size in-memory ring of cheap, already-sampled
/// responsiveness data at 10Hz, the substrate #297 (trigger rules), #298 (ETW circular capture),
/// #299 (incident export) and #300 (incident replay) all build on. See FlightRecorderService for
/// the ring buffer itself.
/// </summary>

/// <summary>One 10Hz sample - every field here is read from a property some earlier chunk in this
/// domain already computes each tick (ResponsivenessViewModel/PerformanceViewModel), never
/// resampled independently. Nullable fields are null when the underlying opt-in probe (present-
/// monitor for FrameTimeMs, the input-latency probe for InputDelayMs) isn't currently running -
/// never a fabricated 0, per CLAUDE.md's degrade rule.</summary>
public sealed record FlightRecorderSample(
    DateTime TimestampUtc,
    double CpuPercent,
    /// <summary>#204-206: the highest per-core "% DPC time" reading this tick (PerCoreDpcService,
    /// always live) - a percentage, not the ETW-measured microsecond figure (#202), since that
    /// only exists while the opt-in DPC measurement session is running. Documented distinctly so a
    /// CSV reader doesn't confuse the two.</summary>
    double MaxCoreDpcPercent,
    double ProcessorQueueLength,
    /// <summary>#278: Memory\Pages Input/sec - 0 is a legitimate reading (no hard faults right
    /// now), distinct from "not available" which just carries the last-known value forward rather
    /// than faking a gap.</summary>
    double HardFaultsPerSec,
    double? FrameTimeMs,
    double? InputDelayMs,
    string ForegroundProcessName,
    string ForegroundWindowTitle,
    /// <summary>Top few processes by CPU% this tick, pre-formatted ("chrome 34%, msedge 12%") -
    /// denormalized into one text field rather than a nested list, so the CSV export (#296/#299)
    /// stays one row per sample with a fixed column count.</summary>
    string TopProcessesText);

/// <summary>#296: the flight recorder's own persisted preference - only the armed/disarmed toggle
/// survives a restart (per the item's own text); the ring buffer's actual contents are always
/// rebuilt fresh in memory, never persisted.</summary>
public sealed class FlightRecorderSettings
{
    public bool Armed { get; set; }
    public static FlightRecorderSettings Defaults => new();
}

/// <summary>#297: what a stutter-trigger rule watches for.</summary>
public enum StutterTriggerKind
{
    FrameTimeOverMs,
    DpcLatencyOverUs,
    WindowHung,
    HardFaultsOverPerSec,
    RunQueueOver,
}

/// <summary>#297: one user-configurable trigger condition. Plain mutable properties (not
/// INotifyPropertyChanged) - simple two-way XAML bindings still work for editing in place, and the
/// list is small/read-in-full each time, so there's no derived/computed display text elsewhere
/// that would need change notification to stay in sync.</summary>
public sealed class StutterTriggerRule
{
    public StutterTriggerKind Kind { get; set; } = StutterTriggerKind.FrameTimeOverMs;
    public double Threshold { get; set; } = 50;
    public bool IsEnabled { get; set; } = true;

    public static StutterTriggerRule Default(StutterTriggerKind kind) => kind switch
    {
        StutterTriggerKind.FrameTimeOverMs => new StutterTriggerRule { Kind = kind, Threshold = 50, IsEnabled = false },
        StutterTriggerKind.DpcLatencyOverUs => new StutterTriggerRule { Kind = kind, Threshold = 2000, IsEnabled = false },
        StutterTriggerKind.WindowHung => new StutterTriggerRule { Kind = kind, Threshold = 0, IsEnabled = true },
        StutterTriggerKind.HardFaultsOverPerSec => new StutterTriggerRule { Kind = kind, Threshold = 200, IsEnabled = false },
        StutterTriggerKind.RunQueueOver => new StutterTriggerRule { Kind = kind, Threshold = 20, IsEnabled = false },
        _ => new StutterTriggerRule { Kind = kind },
    };
}

/// <summary>#297: the trigger-rules settings file. #298's ETW circular-capture toggle is
/// deliberately *not* persisted here (or anywhere) - it always starts back at off on a fresh
/// launch, matching "off by default" literally every session, not just on first install (a wpr.exe
/// circular session obviously can't survive an app restart anyway, so there'd be nothing real to
/// resume even if the preference were remembered).</summary>
public sealed class StutterTriggerRulesSettings
{
    public List<StutterTriggerRule> Rules { get; set; } = DefaultRules();

    public static StutterTriggerRulesSettings Defaults => new() { Rules = DefaultRules() };

    private static List<StutterTriggerRule> DefaultRules() => new()
    {
        StutterTriggerRule.Default(StutterTriggerKind.WindowHung),
        StutterTriggerRule.Default(StutterTriggerKind.FrameTimeOverMs),
        StutterTriggerRule.Default(StutterTriggerKind.DpcLatencyOverUs),
        StutterTriggerRule.Default(StutterTriggerKind.HardFaultsOverPerSec),
        StutterTriggerRule.Default(StutterTriggerKind.RunQueueOver),
    };
}
