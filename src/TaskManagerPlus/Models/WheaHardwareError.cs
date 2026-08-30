using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>#487: broad classification of which WHEA error source produced a
/// Microsoft-Windows-WHEA-Logger record, decoded from the record's own CPER (Common Platform Error
/// Record, UEFI spec Appendix N) section-type GUIDs - see Services.CperDecoder. "Other" covers
/// section types this app doesn't specifically decode; "Unknown" means the binary record itself
/// couldn't be decoded at all (see WheaHardwareErrorEvent.StructuredDecodeSucceeded).</summary>
public enum WheaErrorSourceType
{
    Unknown,
    MachineCheck,
    PciExpress,
    PlatformMemory,
    Nmi,
    Other,
}

/// <summary>#487: the CPER record's own ErrorSeverity field (UEFI spec Appendix N.2.1) - a
/// structured, documented 4-value enum read straight off the wire, not a guess from message text.
/// When the binary record can't be decoded, this falls back to a Level-derived estimate instead
/// (see EventLogService.ReadWheaHardwareErrors).</summary>
public enum WheaErrorSeverity
{
    Unknown,
    Recoverable,
    Fatal,
    Corrected,
    Informational,
}

/// <summary>
/// #487: one Microsoft-Windows-WHEA-Logger event (any event ID - #447's existing event-47-only
/// memory-error read stays as its own narrower, message-text-based display; this is the broad
/// "every hardware error record" view, and will include event 47's records too - now cross-checked
/// against their own binary payload rather than conflicting with #447's reading). SourceType/
/// Severity/Component come straight off the CPER record's own structured fields (ErrorSeverity,
/// NotifyType GUID, section-type GUIDs) when the record's binary payload decodes successfully;
/// when it doesn't (a shape this app's decoder doesn't recognize, or the raw payload couldn't be
/// retrieved from the event at all), this falls back to a Level-derived severity guess and an
/// Unknown source rather than fabricating a category - RawMessage is always populated either way.
/// </summary>
public sealed class WheaHardwareErrorEvent
{
    public DateTime TimeCreated { get; init; }
    public int EventId { get; init; }
    public WheaErrorSourceType SourceType { get; init; } = WheaErrorSourceType.Unknown;
    public WheaErrorSeverity Severity { get; init; } = WheaErrorSeverity.Unknown;
    public string Component { get; init; } = "Unknown";
    public string RawMessage { get; init; } = string.Empty;

    /// <summary>True when CperDecoder successfully parsed this record's binary CPER payload - drives
    /// whether SourceType/Severity above reflect the record's own structured fields (true) or a
    /// Level-based fallback estimate (false).</summary>
    public bool StructuredDecodeSucceeded { get; init; }

    /// <summary>#489: populated only when a PCI Express Error Section was found in this record.</summary>
    public PcieAerDetail? Pcie { get; init; }

    /// <summary>#490: populated only when an IA32/X64 Processor Error Section was found in this
    /// record.</summary>
    public MachineCheckDetail? MachineCheck { get; init; }
}

/// <summary>
/// #489: PCI Express Advanced Error Reporting (AER) detail decoded from a WHEA-Logger record's PCI
/// Express Error Section - Segment/Bus/Device/Function identify the reporting device (the CPER
/// section's own "Device ID" sub-structure). FriendlyDeviceName is a best-effort match against a
/// present device's own reported location (see PnpDeviceTreeService.BuildPciLocationLookup) - null
/// when no present device's location string matches this bus/device/function (the device may no
/// longer be present, or sit behind a bridge this app's lookup doesn't resolve). StatusFlags are
/// the AER Correctable/Uncorrectable Error Status register's own set bits, decoded against the PCI
/// Express Base Specification's documented, stable bit assignments - the hardware's own report, not
/// an interpretation of it.
/// </summary>
public sealed class PcieAerDetail
{
    public int Segment { get; init; }
    public int Bus { get; init; }
    public int Device { get; init; }
    public int Function { get; init; }
    public string? FriendlyDeviceName { get; init; }
    public int? VendorId { get; init; }
    public int? DeviceId { get; init; }
    public bool IsUncorrectable { get; init; }
    public IReadOnlyList<string> StatusFlags { get; init; } = Array.Empty<string>();

    public string BdfText => $"{Segment:x4}:{Bus:x2}:{Device:x2}.{Function:x1}";
    public string SeverityClassText => IsUncorrectable ? "Uncorrectable" : "Correctable";
    public string VendorIdText => VendorId is { } v ? $"0x{v:x4}" : "Unknown";
    public string DeviceIdText => DeviceId is { } v ? $"0x{v:x4}" : "Unknown";
}

/// <summary>
/// #490: processor machine-check detail decoded from a WHEA-Logger record's IA32/X64 Processor
/// Error Section. Bank/RawMciStatus/Uncorrected/ProcessorContextCorrupt/Overflow are read straight
/// off the CPU's own reported IA32_MCi_STATUS register when the record captured one verbatim (see
/// CperDecoder) - "the hardware reported this," not an interpretation of it. When no raw register
/// was captured, Uncorrected/ProcessorContextCorrupt/Overflow instead come from the CPER record's
/// own decoded check-info flags (UEFI spec Appendix N's "MS Check" structure), and Bank/
/// RawMciStatus stay null rather than guessed. ApicId comes from the section's own Local APIC ID
/// field independent of any bank being found.
/// </summary>
public sealed class MachineCheckDetail
{
    public int? Bank { get; init; }
    public ulong? RawMciStatus { get; init; }
    public ulong? ApicId { get; init; }
    public bool? Uncorrected { get; init; }
    public bool? ProcessorContextCorrupt { get; init; }
    public bool? Overflow { get; init; }

    public string BankText => Bank is { } b ? b.ToString() : "Unknown";
    public string ApicIdText => ApicId is { } a ? a.ToString() : "Unknown";
    public string RawMciStatusText => RawMciStatus is { } v ? $"0x{v:x16}" : "not captured by this record";
    public string UncorrectedText => Uncorrected is { } u ? (u ? "Yes" : "No") : "Unknown";
    public string ProcessorContextCorruptText => ProcessorContextCorrupt is { } p ? (p ? "Yes" : "No") : "Unknown";
    public string OverflowText => Overflow is { } o ? (o ? "Yes" : "No") : "Unknown";
}

/// <summary>
/// #492: one crash/TDR/unexpected-shutdown event that had at least one WHEA hardware-error record
/// within EventLogService's correlation window beforehand - StabilityViewModel overlays these onto
/// the existing crash timeline. Explicitly framed as a correlation, not a claimed cause: a hardware
/// error shortly before a crash is suggestive, not proof the two are related - the crash could
/// easily be an unrelated software fault, and a genuinely-related hardware error can also precede
/// its crash by longer than the window used here.
/// </summary>
public sealed class HardwareErrorCorrelation
{
    public DateTime CrashTime { get; init; }
    public string CrashDescription { get; init; } = string.Empty;
    public DateTime HardwareErrorTime { get; init; }
    public string HardwareErrorDescription { get; init; } = string.Empty;
    public TimeSpan Gap { get; init; }

    /// <summary>How many WHEA records (not just the nearest one shown above) fell inside the
    /// correlation window before this crash.</summary>
    public int HardwareErrorsInWindow { get; init; }

    public string GapText => Gap.TotalSeconds < 60
        ? $"{Math.Max(0, (int)Gap.TotalSeconds)}s earlier"
        : $"{Formatting.FormatSpanMinutes(Gap)} earlier";
}
