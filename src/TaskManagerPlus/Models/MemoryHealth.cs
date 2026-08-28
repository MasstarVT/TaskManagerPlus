namespace TaskManagerPlus.Models;

/// <summary>
/// #440: one Memory Device (SMBIOS Type 17) structure, decoded straight from the raw firmware
/// table via <see cref="TaskManagerPlus.Services.SmbiosMemoryService"/> - the fields WMI's
/// Win32_PhysicalMemory drops entirely (part number, serial number, rank, voltage, form factor,
/// bank locator, memory technology). Matched back to a WMI-sourced MemoryModuleInfo by
/// DeviceLocator in SystemSpecsService. Every field is "Unknown"/null when the running BIOS's
/// SMBIOS version predates it (a 2.4-era BIOS simply has a shorter Type 17 structure with no
/// voltage/technology bytes at all) or the raw table couldn't be read/parsed at all - never a
/// guessed value.
/// </summary>
public sealed class SmbiosMemoryDevice
{
    public string DeviceLocator { get; init; } = string.Empty;
    public string BankLocator { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public string PartNumber { get; init; } = string.Empty;
    public string AssetTag { get; init; } = string.Empty;

    /// <summary>Total data-transfer width in bits, including any ECC/check bits (e.g. 72 for an
    /// ECC DDR4/DDR5 module, vs. 64 for the same module's DataWidthBits) - #446's per-module ECC
    /// evidence, alongside the array-level Win32_PhysicalMemoryArray.MemoryErrorCorrection field.</summary>
    public int? TotalWidthBits { get; init; }
    public int? DataWidthBits { get; init; }

    public long SizeBytes { get; init; }
    public string FormFactor { get; init; } = "Unknown";
    public string MemoryType { get; init; } = "Unknown";

    /// <summary>Rated maximum speed the module reports capability for (MT/s) - SMBIOS's own
    /// "Speed" field, the same underlying figure Win32_PhysicalMemory.Speed already surfaces.</summary>
    public int? RatedSpeedMts { get; init; }

    /// <summary>The speed firmware actually configured the module to run at (MT/s, SMBIOS 2.7+) -
    /// the same underlying figure Win32_PhysicalMemory.ConfiguredClockSpeed already surfaces.</summary>
    public int? ConfiguredSpeedMts { get; init; }

    /// <summary>Rank count (SMBIOS 2.6+ Attributes field, low nibble) - not available from WMI at all.</summary>
    public int? RankCount { get; init; }

    public double? MinVoltageV { get; init; }
    public double? MaxVoltageV { get; init; }
    public double? ConfiguredVoltageV { get; init; }

    /// <summary>SMBIOS 3.2+ only ("DRAM", "NVDIMM-N", ...) - "Unknown" on any BIOS older than that,
    /// which is most systems in practice.</summary>
    public string MemoryTechnology { get; init; } = "Unknown";
}

/// <summary>#447: one Microsoft-Windows-WHEA-Logger corrected-memory-error event (event ID 47) -
/// DIMM/physical-address hints are best-effort regex extraction from the event's own formatted
/// message text (not a documented, versioned contract - the same caveat
/// EventLogService.ExtractBugcheckCode already documents for a different event), so both are null
/// when the message doesn't match a recognized shape rather than a guess.</summary>
public sealed class CorrectedMemoryErrorEvent
{
    public DateTime TimeCreated { get; init; }
    public string? DimmHint { get; init; }
    public string? PhysicalAddressHint { get; init; }
    public string RawMessage { get; init; } = string.Empty;
}

/// <summary>#449: the most recent Windows Memory Diagnostic (mdsched.exe) run result, read from
/// the System log's MemoryDiagnostics-Results source (event IDs 1101/1201) - null (shown as
/// "never run") when no such event exists in the retained log window, not "passed".</summary>
public sealed class MemoryDiagnosticResultInfo
{
    public DateTime TimeCreated { get; init; }

    /// <summary>True = no errors found, False = errors found, null = the result event was found
    /// but its outcome text didn't match either recognized pattern.</summary>
    public bool? Passed { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

/// <summary>
/// #451: rolls up mismatched DIMMs (#443), ECC status (#446), corrected ECC errors (#447), the
/// memory diagnostic result (#449), XMP/rated-vs-running state (#442), channel population (#444)
/// and memory-related bugchecks (from the Stability tab's own event scan) into one verdict.
/// "Quick flag, not a verdict" - Findings lists exactly what triggered it; never presented as a
/// certified hardware diagnosis, the same honesty tier as this app's other multi-signal rollups
/// (StabilityViewModel.ComputeStabilityIndex, the thrashing detector, ...).
/// </summary>
public sealed class RamHealthSummary
{
    public string Verdict { get; init; } = "Unknown";
    public bool IsWarning { get; init; }
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
}
