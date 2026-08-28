namespace TaskManagerPlus.Models;

/// <summary>Round 12, #90: one entry from `powercfg /list` - the built-in Windows power schemes
/// (Balanced/High performance/Power saver, plus any OEM- or user-added scheme).</summary>
public sealed class PowerPlanInfo
{
    public string Guid { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}

/// <summary>#691: one of the three well-known Windows power-mode overlay schemes ("Best power
/// efficiency" / "Balanced" / "Best performance" - the slider next to the battery icon on Windows
/// 10 1709+/11) - a plain CLR class rather than exposing PowerPlanService.OverlaySchemes' tuples
/// directly to XAML, since a ValueTuple's element names ("Guid"/"Name") are compile-time-only
/// aliases erased at runtime and can't be data-bound (see UsbPdConnectorInfo's own remarks on the
/// same trap).</summary>
public sealed class PowerOverlaySchemeOption
{
    public string Guid { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

/// <summary>Round 12, #92: best-effort per-USB-device selective-suspend read - see
/// UsbPowerService's remarks for why <see cref="SelectiveSuspendEnabled"/> is nullable
/// ("Unknown") far more often than a definitive true/false on real hardware.</summary>
public sealed class UsbDevicePowerInfo
{
    public string Name { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public bool? SelectiveSuspendEnabled { get; init; }

    /// <summary>#668: the device-class hint UsbPowerService.ClassifyRisk derived from this
    /// device's WMI ClassGuid (e.g. "USB audio interface/DAC", "HID input", "External storage") -
    /// empty when the device isn't one of the classes known to break under selective suspend.</summary>
    public string RiskClass { get; init; } = string.Empty;

    /// <summary>#668: true when this device belongs to a RiskClass AND selective suspend is
    /// currently enabled for it - "quick flag, not a verdict" (plenty of devices in these classes
    /// suspend fine; this only means it's worth checking if something in that class is
    /// misbehaving). Never true while SelectiveSuspendEnabled is Unknown.</summary>
    public bool IsSuspendRisk => RiskClass.Length > 0 && SelectiveSuspendEnabled == true;

    /// <summary>#667: count of surprise-removal/re-arrival event-log records
    /// UsbEventLogService.ReadReenumerationCountsAsync matched to this device's PNPDeviceID over
    /// the lookback window - set after the fact by EnergyThermalsViewModel once both the device
    /// list and the event scan have loaded (mutable, not init-only, for exactly that reason). -1
    /// means "not scanned yet" (distinct from a confirmed 0).</summary>
    public int ReenumerationCount { get; set; } = -1;
}
