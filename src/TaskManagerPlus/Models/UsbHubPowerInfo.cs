namespace TaskManagerPlus.Models;

/// <summary>#666: one USB hub's real inventory data (Win32_USBHub) - see UsbHubPowerService's
/// remarks for why this is hub inventory plus a system-wide device-vs-port-capacity proxy rather
/// than a true per-hub milliamp power budget. Windows doesn't publish a documented WMI class or
/// registry value for a hub's available current or a device's descriptor-level MaxPower draw (that
/// data lives only in the raw USB descriptor, reachable solely via hub IOCTL interop - a
/// materially larger native-interop undertaking this app deliberately avoids, the same judgment
/// call UsbPowerService's own remarks make for selective-suspend status), nor a reliable WMI-only
/// per-hub parent/child device association short of that same interop.</summary>
public sealed class UsbHubPowerInfo
{
    public string HubName { get; init; } = string.Empty;
    public string PnpDeviceId { get; init; } = string.Empty;

    /// <summary>Null when Win32_USBHub didn't report a port count for this hub.</summary>
    public int? PortCount { get; init; }
}
