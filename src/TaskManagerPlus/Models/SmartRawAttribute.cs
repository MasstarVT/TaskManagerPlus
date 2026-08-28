namespace TaskManagerPlus.Models;

/// <summary>
/// One decoded raw ATA SMART attribute (#301) from the 512-byte VendorSpecific blob in
/// MSStorageDriver_ATAPISmartData (root\wmi) - the standard ATA-8 30-entry attribute table, not
/// the driver-summarised subset MSFT_StorageReliabilityCounter exposes. Paired with its published
/// failure threshold from MSStorageDriver_FailurePredictThresholds (#302, Margin/ThresholdText),
/// a friendly name from SmartAttributeLookup/SmartVendorProfiles (#303/#304), and - for the
/// handful of IDs a matched vendor profile knows how to reinterpret (#304/#305) - a vendor-decoded
/// RawDisplay string instead of the plain 48-bit integer.
/// </summary>
public sealed class SmartRawAttribute
{
    public byte Id { get; init; }
    public string IdHex => $"{Id:X2}";
    public string Name { get; init; } = string.Empty;
    public ushort Flags { get; init; }
    public byte Current { get; init; }
    public byte Worst { get; init; }
    public byte[] RawBytes { get; init; } = new byte[6];

    /// <summary>The 6 raw bytes as a little-endian 48-bit integer - the plain, un-decoded value.</summary>
    public ulong RawValue { get; init; }

    /// <summary>Published failure threshold for this ID, when the driver reports one. Null (never
    /// a fabricated 0) when MSStorageDriver_FailurePredictThresholds has no entry for it.</summary>
    public byte? Threshold { get; init; }

    /// <summary>Current - Threshold (#302); the raw SMART grid sorts ascending by this so the
    /// closest-to-failure attribute is row one. Null (renders "—") when there's no threshold.</summary>
    public int? Margin { get; init; }

    public string ThresholdText => Threshold.HasValue ? Threshold.Value.ToString() : "—";
    public string MarginText => Margin.HasValue ? Margin.Value.ToString() : "—";

    /// <summary>Human-readable raw value - either the plain 48-bit integer, or (#305) a
    /// vendor-decoded split such as Seagate's 16-bit-error/32-bit-operation-count pair.</summary>
    public string RawDisplay { get; init; } = string.Empty;

    /// <summary>Set only when RawDisplay came from a vendor-specific reinterpretation rather than
    /// the plain raw integer, e.g. "decoded as Seagate 16/32-bit split" - shown next to the value
    /// so the user knows which interpretation produced it (informational, not authoritative).</summary>
    public string? VendorNote { get; init; }
}
