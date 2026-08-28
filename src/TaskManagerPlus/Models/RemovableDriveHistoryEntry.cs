namespace TaskManagerPlus.Models;

/// <summary>
/// #375: one device ever recorded under the USBSTOR or SWD\WPDBUSENUM registry branches - "every
/// storage device ever attached", not just what's currently plugged in (see
/// RemovableDriveHistoryService). FirstConnected/LastConnected are best-effort reads of the
/// standard PnP device-properties registry shape and are null whenever that specific decode fails
/// or the property was never recorded - FriendlyName/Serial are the reliable part and are always
/// populated (falling back to the raw registry key name when Windows never recorded a friendlier
/// description).
/// </summary>
public sealed class RemovableDriveHistoryEntry
{
    public string FriendlyName { get; init; } = "Unknown device";
    public string Serial { get; init; } = string.Empty;

    /// <summary>"USB mass storage" (USBSTOR) or "Portable device (WPD)" (SWD\WPDBUSENUM).</summary>
    public string Source { get; init; } = string.Empty;

    public DateTime? FirstConnected { get; init; }
    public DateTime? LastConnected { get; init; }
}
