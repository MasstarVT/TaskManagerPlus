namespace TaskManagerPlus.Models;

/// <summary>#669: one instance of whatever UCSI (USB Type-C Connector System Software Interface)
/// WMI class this system happens to publish under root\wmi - see UsbPdService's remarks for why
/// this is a generic property dump rather than a fixed Voltage/Current/PowerRole mapping. Windows
/// has never published a stable, versioned class-name/property contract for it the way it has for
/// e.g. MSPower_DeviceEnable, so this reports exactly what the class exposes, under whatever names
/// it actually uses, rather than guessing at named fields this app can't verify exist on real
/// hardware.</summary>
public sealed class UsbPdConnectorInfo
{
    /// <summary>The root\wmi class name this instance was found under, e.g. whatever this
    /// platform's UCSI driver happens to publish - shown so the raw property list below is at
    /// least attributable to a specific source.</summary>
    public string ClassName { get; init; } = string.Empty;

    /// <summary>All of this instance's WMI properties as raw key/value rows, in the order WMI
    /// reported them. A plain class (not a value tuple) so XAML data-binding can actually resolve
    /// Name/Value as real CLR properties - a ValueTuple's element names are a compile-time-only
    /// alias over Item1/Item2 and aren't visible to WPF's binding reflection.</summary>
    public List<UsbPdProperty> Properties { get; init; } = new();

    /// <summary>Best-effort one-line summary built from whichever properties looked like a
    /// voltage/current/power-role reading by name alone (see UsbPdService.Summarize) - empty when
    /// nothing in Properties looked like one, in which case only the raw dump is shown.</summary>
    public string SummaryText { get; init; } = string.Empty;
}

/// <summary>One raw WMI property name/value pair - see UsbPdConnectorInfo.Properties' remarks.</summary>
public sealed class UsbPdProperty
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}
