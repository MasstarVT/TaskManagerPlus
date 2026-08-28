namespace TaskManagerPlus.Models;

/// <summary>
/// #636: one WHEA-Logger hardware-error event (Windows Hardware Error Architecture) - fatal
/// machine checks (event 1), corrected machine checks (17), corrected platform/PCIe errors
/// (18/19/20), and corrected memory errors (47). The app has no WHEA surface before this round;
/// see EventLogService.ReadWheaEvents for exactly which fields are parsed from the formatted
/// message text (best-effort - WHEA-Logger's message layout isn't a documented, versioned
/// contract any more than Kernel-Power 41's insertion-string layout is, see
/// EventLogService.ExtractBugcheckCode's remarks for the same caveat) and which are honestly left
/// null/empty when the message doesn't match the expected shape.
/// </summary>
public sealed class WheaEvent
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }

    /// <summary>True only for event 1 (fatal/uncorrected machine check) - every other event ID
    /// this app reads (17/18/19/20/47) is a *corrected* error, logged purely for trend visibility.</summary>
    public bool IsFatal { get; init; }

    public string CategoryText { get; init; } = string.Empty;

    /// <summary>Best-effort "Error Source:" line from the formatted message - empty when the
    /// message doesn't contain one.</summary>
    public string ErrorSourceText { get; init; } = string.Empty;

    /// <summary>Best-effort "Bank Number:" line (machine-check events only) - null when absent
    /// (a platform/PCIe event, or a message layout this parser doesn't recognize).</summary>
    public int? Bank { get; init; }

    /// <summary>#640: short plain-English interpretation of ErrorSourceText/Bank, from
    /// MceBankHintLookup - "quick interpretation, not a verdict," rendered inline in the WHEA list.</summary>
    public string BankHintText { get; init; } = string.Empty;

    /// <summary>#639: PCIe location, present only for events whose message included a
    /// Segment/Bus/Device/Function line - null for a machine-check or memory event.</summary>
    public int? PcieSegment { get; init; }
    public int? PcieBus { get; init; }
    public int? PcieDevice { get; init; }
    public int? PcieFunction { get; init; }

    /// <summary>#639: "GPU (PCI\VEN_10DE...)" - resolved against Win32_PnPEntity by
    /// PciDeviceResolverService when a PCIe location was parsed and a matching device was found.
    /// Empty when there's no PCIe location, or no device resolved to it.</summary>
    public string ResolvedDeviceName { get; init; } = string.Empty;
    public string ResolvedDeviceId { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

/// <summary>#638: one row of the WHEA card's "conditions at the moment of each error" table - each
/// WHEA event timestamp joined against the nearest PowerHistoryLogService sample. Null fields mean
/// no sample fell within the join's tolerance window (a fresh install with little history yet, or
/// a gap while the app wasn't running) - shown as "Unknown," never fabricated.</summary>
public sealed class WheaConditionRow
{
    public DateTime TimeCreated { get; init; }
    public string ErrorSummary { get; init; } = string.Empty;
    public double? TempCAtEvent { get; init; }
    public double? PowerWAtEvent { get; init; }
}
