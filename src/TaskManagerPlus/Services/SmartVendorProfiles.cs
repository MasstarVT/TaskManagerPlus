namespace TaskManagerPlus.Services;

/// <summary>One vendor's reinterpretation of the vendor-reassigned SMART attribute IDs (#304),
/// matched to a disk by its Win32_DiskDrive.Model prefix. Purely informational data, not a
/// per-model spec sheet - the rated-cycle figures in particular are generic "what's typical for
/// this vendor's consumer HDD line" numbers (#309), not looked up per exact model number.</summary>
public sealed class SmartVendorProfile
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Attribute-ID overrides for this vendor - takes priority over
    /// SmartAttributeLookup's generic ATA-8 name for the same ID.</summary>
    public Dictionary<byte, string> AttributeNames { get; init; } = new();

    /// <summary>Typical (not per-model) rated maximum for Load/Unload Cycle Count (0xC1) on this
    /// vendor's consumer HDD line - #309. Null when this vendor doesn't make (or this profile
    /// doesn't cover) spinning HDDs.</summary>
    public int? TypicalLoadUnloadRatedMax { get; init; }

    /// <summary>Typical (not per-model) rated maximum for Start/Stop Count (0x04) - #309.</summary>
    public int? TypicalStartStopRatedMax { get; init; }
}

/// <summary>
/// #304: JSON-shaped-in-code map of vendor SMART attribute conventions, keyed by
/// Win32_DiskDrive.Model prefix - the same "small static lookup, not user-editable settings" data
/// tradeoff SmartAttributeLookup/JedecManufacturerLookup already take. Attributes 0xAD/0xB1/0xE8/
/// 0xE9/0xF1/0xF2 in particular are reassigned to different meanings by different SSD controllers,
/// so applying the wrong vendor's table would be actively misleading - this is applied
/// automatically per disk (SmartRawAttributeService.Read matches by Model), and the matched
/// profile's name is always shown next to the raw SMART grid so the user can see which
/// interpretation produced the numbers.
/// </summary>
public static class SmartVendorProfiles
{
    private static readonly SmartVendorProfile Samsung = new()
    {
        Name = "Samsung",
        AttributeNames = new Dictionary<byte, string>
        {
            [0xB1] = "Wear Leveling Count",
            [0xB2] = "Used Reserved Block Count (Chip)",
            [0xB3] = "Used Reserved Block Count (Total)",
            [0xB4] = "Unused Reserved Block Count (Total)",
            [0xB5] = "Program Fail Count (Total)",
            [0xB6] = "Erase Fail Count (Total)",
            [0xB7] = "SATA Downshift Count",
            [0xBB] = "Uncorrectable Error Count",
            [0xC3] = "ECC Error Rate",
            [0xC7] = "CRC Error Count",
            [0xEB] = "POR Recovery Count",
            [0xF9] = "Total LBAs Written",
        },
    };

    private static readonly SmartVendorProfile CrucialMicron = new()
    {
        Name = "Crucial/Micron",
        AttributeNames = new Dictionary<byte, string>
        {
            [0xAB] = "Program Fail Count",
            [0xAD] = "Wear Leveling Count",
            [0xB1] = "Wear Range Delta",
            [0xE8] = "Available Reserved Space",
            [0xE9] = "Media Wearout Indicator (% life used)",
            [0xF6] = "Minimum Erase Count",
            [0xF7] = "Maximum Erase Count",
            [0xF8] = "Average Erase Count",
            [0xF1] = "Total LBAs Written",
            [0xF2] = "Total LBAs Read",
        },
    };

    private static readonly SmartVendorProfile WesternDigital = new()
    {
        Name = "Western Digital",
        AttributeNames = new Dictionary<byte, string>
        {
            [0xC1] = "Load Cycle Count",
            [0xAA] = "Available Reserved Space",
            [0xAB] = "SSD Program Fail Count",
            [0xAC] = "SSD Erase Fail Count",
            [0xAD] = "SSD Wear Leveling Count",
            [0xB5] = "SSD Program Fail Count (Total)",
            [0xB6] = "SSD Erase Fail Count (Total)",
            [0xB7] = "SATA Downshift Error Count",
            [0xF1] = "Total LBAs Written",
            [0xF2] = "Total LBAs Read",
        },
        // Consumer 5400/7200rpm 2.5"/3.5" desktop/laptop drives - not enterprise/NAS-rated lines,
        // which run higher. Informational ballpark only (#309), never presented as this exact
        // drive's spec.
        TypicalLoadUnloadRatedMax = 300000,
        TypicalStartStopRatedMax = 50000,
    };

    private static readonly SmartVendorProfile Seagate = new()
    {
        Name = "Seagate",
        AttributeNames = new Dictionary<byte, string>
        {
            // #305: raw field is a 16-bit error count over a 32-bit operation count, not a plain
            // integer - see DecodeRaw below.
            [0x01] = "Raw Read Error Rate",
            [0x07] = "Seek Error Rate",
            [0xB8] = "End-to-End Error Count",
            [0xBC] = "Command Timeout",
            [0xBD] = "High Fly Writes",
            [0xE8] = "Available Reserved Space",
            [0xE9] = "Media Wearout Indicator (% life used)",
            [0xF1] = "Total LBAs Written",
            [0xF2] = "Total LBAs Read",
        },
        TypicalLoadUnloadRatedMax = 300000,
        TypicalStartStopRatedMax = 50000,
    };

    private static readonly SmartVendorProfile IntelSolidigm = new()
    {
        Name = "Intel/Solidigm",
        AttributeNames = new Dictionary<byte, string>
        {
            [0xAD] = "NAND Program Fail Count",
            [0xE2] = "Timed Workload Media Wear",
            [0xE8] = "Available Reserved Space",
            [0xE9] = "Media Wearout Indicator (% life used)",
            [0xF1] = "Total LBAs Written",
            [0xF2] = "Total LBAs Read",
        },
    };

    private static readonly SmartVendorProfile SkHynix = new()
    {
        Name = "SK hynix",
        AttributeNames = new Dictionary<byte, string>
        {
            [0xB1] = "Wear Range Delta",
            [0xE8] = "Available Reserved Space",
            [0xE9] = "Media Wearout Indicator (% life used)",
            [0xF1] = "Total LBAs Written",
            [0xF2] = "Total LBAs Read",
        },
    };

    private static readonly SmartVendorProfile KingstonPhison = new()
    {
        Name = "Kingston/Phison",
        AttributeNames = new Dictionary<byte, string>
        {
            [0xAD] = "Wear Leveling Count",
            [0xE7] = "SSD Life Left",
            [0xE9] = "Media Wearout Indicator (% life used)",
            [0xF1] = "Total LBAs Written",
            [0xF2] = "Total LBAs Read",
        },
    };

    private static readonly SmartVendorProfile SanDisk = new()
    {
        Name = "SanDisk",
        AttributeNames = new Dictionary<byte, string>
        {
            [0xE7] = "SSD Life Left",
            [0xE9] = "Media Wearout Indicator (% life used)",
            [0xF1] = "Total LBAs Written",
            [0xF2] = "Total LBAs Read",
        },
    };

    // Ordered so a more specific prefix (e.g. "Solidigm") is tried before a shorter, broader one
    // could ever accidentally shadow it - StartsWith on Win32_DiskDrive.Model, not a substring
    // Contains, since Seagate's own "ST" prefix in particular would false-positive against many
    // unrelated model strings if matched as a substring anywhere in the name.
    private static readonly (string Prefix, SmartVendorProfile Profile)[] Profiles =
    {
        ("Samsung", Samsung),
        ("Crucial", CrucialMicron),
        ("Micron", CrucialMicron),
        ("MTFD", CrucialMicron), // Micron's own OEM model-number prefix
        ("WDC", WesternDigital),
        ("WD ", WesternDigital),
        ("HGST", WesternDigital), // WD-owned since 2012; shares WD's SMART attribute conventions closely enough for this informational table
        ("Solidigm", IntelSolidigm),
        ("Intel", IntelSolidigm),
        ("SK hynix", SkHynix),
        ("Hynix", SkHynix),
        ("Kingston", KingstonPhison),
        ("Phison", KingstonPhison),
        ("SanDisk", SanDisk),
        ("ST", Seagate), // Seagate model numbers universally start with "ST" (ST1000LM048, ...)
        ("Seagate", Seagate),
    };

    public static SmartVendorProfile? Match(string model)
    {
        var trimmed = model?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return null;

        foreach (var (prefix, profile) in Profiles)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return profile;
        }
        return null;
    }

    /// <summary>
    /// #305: Seagate's 0x01 (Raw Read Error Rate) and 0x07 (Seek Error Rate) pack a 16-bit error
    /// count above a 32-bit operation count within the 48-bit raw field, rather than being a plain
    /// integer - decoded as a plain number it reads as "billions of errors", which is the raw
    /// operation count, not an error count, and panics people. Only applied when the matched
    /// vendor is Seagate; every other vendor's raw field for these two IDs is a plain count.
    /// Captioned "decoded as Seagate 16/32-bit split" wherever it's shown.
    /// </summary>
    public static (string Display, string? Note) DecodeRaw(byte id, ulong rawValue, SmartVendorProfile? profile)
    {
        if (ReferenceEquals(profile, Seagate) && (id == 0x01 || id == 0x07))
        {
            uint operations = (uint)(rawValue & 0xFFFFFFFF);
            ushort errors = (ushort)((rawValue >> 32) & 0xFFFF);
            return ($"{errors} errors / {operations:N0} ops", "decoded as Seagate 16/32-bit split");
        }
        return (rawValue.ToString("N0"), null);
    }
}
