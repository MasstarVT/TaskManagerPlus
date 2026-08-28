namespace TaskManagerPlus.Models;

/// <summary>#665: one over-current or port-reset-failure record scanned from the
/// Microsoft-Windows-USB-USBHUB3/Operational log and the legacy System-log "usbhub"/"USBHUB"
/// source - see UsbEventLogService.ReadOverCurrentEventsAsync's remarks for why this is a
/// keyword-matched scan rather than a fixed-event-ID one.</summary>
public sealed class UsbPowerEvent
{
    public DateTime TimeCreated { get; init; }
    public string ProviderName { get; init; } = string.Empty;

    /// <summary>The best-effort USB device instance ID substring pulled out of the event message
    /// (e.g. "USB\VID_0781&amp;PID_5581\..."), when the message happened to contain one - empty
    /// when it didn't, in which case this event still shows in the raw list but can't be joined
    /// onto a specific row in the device grid.</summary>
    public string DeviceIdHint { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
