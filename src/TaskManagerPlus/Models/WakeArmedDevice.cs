namespace TaskManagerPlus.Models;

/// <summary>#653: one device currently armed to wake the system from sleep - the intersection of
/// `powercfg /devicequery wake_armed` (currently configured to wake) and `wake_from_any` (able to
/// wake the system from any sleep state), which together answer "what could actually wake this PC
/// right now" rather than either list alone (wake_armed alone includes devices only able to wake
/// from a shallow state; wake_from_any alone includes plenty of devices that aren't currently armed
/// at all). See WakeDeviceService.</summary>
public sealed class WakeArmedDevice
{
    public string Name { get; init; } = string.Empty;
}
