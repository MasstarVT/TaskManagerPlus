namespace TaskManagerPlus.Models;

/// <summary>
/// #477: MSI vs. legacy line-based interrupt mode for one device, read from
/// HKLM\SYSTEM\CurrentControlSet\Enum\{deviceId}\Device Parameters\Interrupt Management\
/// MessageSignaledInterruptProperties (MSISupported) and \Affinity Policy (DevicePolicy) - see
/// InterruptModeService. Display-only by design, per the suggestion text: this app offers no
/// action to change either value - doing so is a manual registry edit outside this tab's scope,
/// and getting it wrong can leave a device unable to start at all.
/// </summary>
public sealed class InterruptModeInfo
{
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>Null when MessageSignaledInterruptProperties\MSISupported isn't set at all (most
    /// devices never had this touched) - shown as "not configured", not fabricated as an explicit
    /// "line-based" the driver never actually declared.</summary>
    public bool? MsiSupported { get; init; }
    public string ModeText => MsiSupported switch
    {
        true => "MSI",
        false => "Line-based (legacy)",
        _ => "Not configured (line-based by default)",
    };

    public string AffinityPolicyText { get; init; } = "Machine default";

    /// <summary>#476 cross-reference: the legacy IRQ line this device is on, if any - filled in by
    /// the ViewModel after both the resource map and this scan complete, not computed in
    /// InterruptModeService itself (which has no resource-map dependency of its own).</summary>
    public int? IrqNumber { get; set; }

    /// <summary>How many *other* devices report the same IRQ number - 0 when IrqNumber is null or
    /// this device is the only one on that line.</summary>
    public int SharesLineWithCount { get; set; }

    public string SharingText => IrqNumber is not { } irq
        ? "No legacy IRQ line reported"
        : SharesLineWithCount > 0
            ? $"IRQ {irq} - shared with {SharesLineWithCount} other device(s)"
            : $"IRQ {irq} - not shared";
}
