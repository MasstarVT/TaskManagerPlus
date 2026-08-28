namespace TaskManagerPlus.Models;

/// <summary>#647: one battery-instance presence transition (either LibreHardwareMonitorLib's
/// sensor tree or Win32_Battery going from reporting a battery to not, or back) observed between
/// two ticks this session. A battery flickering in and out of view like this - distinct from a
/// normal unplug/replug, which doesn't make the battery *instance itself* disappear - points at a
/// failing battery connector or gauge IC, a signal that's otherwise invisible since nothing else
/// in Windows surfaces it directly. Session-only (not persisted to disk), the same scope
/// EnergyThermalsViewModel.ThrottleEvents already uses for its own "when did this happen" log.</summary>
public sealed class BatteryPresenceEvent
{
    public DateTime Timestamp { get; init; }

    /// <summary>True = the battery instance reappeared at this tick; false = it disappeared.</summary>
    public bool BecamePresent { get; init; }

    public string DescriptionText => BecamePresent ? "Battery reappeared" : "Battery disappeared";
}
