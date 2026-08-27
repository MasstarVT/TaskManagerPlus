namespace TaskManagerPlus.Services;

/// <summary>
/// Small, non-exhaustive JEDEC manufacturer-ID lookup for RAM modules (Round 8 #36), purely
/// informational. Win32_PhysicalMemory.Manufacturer is usually already a readable string on
/// modern systems ("Kingston", "Samsung", ...), but some motherboard firmware instead reports the
/// raw SPD manufacturer bank/ID byte (a hex JEDEC JEP106 continuation code) or leaves it blank/
/// "Unknown" - this table covers only the handful of manufacturer codes common on consumer
/// desktop/laptop RAM, not a full JEP106 database, so an unmatched or already-readable value just
/// passes through unchanged rather than being forced into a wrong guess.
/// </summary>
public static class JedecManufacturerLookup
{
    // JEP106 last-non-zero continuation byte (as commonly surfaced by SPD/firmware readers), hex,
    // no "0x" prefix - a small, deliberately non-exhaustive set of the manufacturers most likely
    // to show up in a consumer desktop/laptop.
    private static readonly Dictionary<string, string> KnownCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["04"] = "Kingston",
        ["98"] = "Kingston",
        ["2C"] = "Micron",
        ["CE"] = "Samsung",
        ["AD"] = "SK Hynix",
        ["9E"] = "Corsair",
        ["C8"] = "Crucial (Micron)",
        ["CB"] = "Crucial (Micron)",
        ["B0"] = "G.Skill",
        ["25"] = "ADATA",
    };

    public static string Resolve(string rawManufacturer)
    {
        if (string.IsNullOrWhiteSpace(rawManufacturer)) return "Unknown";
        var trimmed = rawManufacturer.Trim();

        // A raw JEDEC code from this table is short and purely hex ("04", "2C", ...); anything
        // else (a plain readable brand name, which is the common case on modern firmware) passes
        // through unchanged.
        if (trimmed.Length > 2 || !trimmed.All(Uri.IsHexDigit))
            return trimmed;

        return KnownCodes.TryGetValue(trimmed, out var name) ? name : trimmed;
    }
}
