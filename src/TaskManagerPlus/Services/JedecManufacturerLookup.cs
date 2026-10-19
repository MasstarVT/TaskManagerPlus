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
    // JEP106 codes, hex, no "0x" prefix - a small, deliberately non-exhaustive set of the
    // manufacturers most likely to show up in a consumer desktop/laptop. Firmware surfaces either
    // the full two-byte SPD form (continuation count with its parity bit, then the ID: Kingston is
    // in bank 2 -> "0198") or just the ID byte ("98"). The full form is unambiguous; the ID byte
    // alone is reused in every bank, so the short table keeps only these consumer brands' IDs.
    private static readonly Dictionary<string, string> KnownFullCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0198"] = "Kingston",
        ["80CE"] = "Samsung",
        ["80AD"] = "SK Hynix",
        ["802C"] = "Micron",
        ["859B"] = "Crucial (Micron)",
        ["04CD"] = "G.Skill",
        ["04CB"] = "ADATA",
        ["029E"] = "Corsair",
        ["04EF"] = "Team Group",
    };

    private static readonly Dictionary<string, string> KnownCodes = KnownFullCodes
        .ToDictionary(kv => kv.Key[2..], kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string rawManufacturer)
    {
        if (string.IsNullOrWhiteSpace(rawManufacturer)) return "Unknown";
        var trimmed = rawManufacturer.Trim();

        // A raw JEDEC code is short and purely hex ("98", "859B", ...); anything else (a plain
        // readable brand name, which is the common case on modern firmware) passes through
        // unchanged, as does a code this table doesn't know.
        if (!trimmed.All(Uri.IsHexDigit))
            return trimmed;

        var table = trimmed.Length switch
        {
            2 => KnownCodes,
            4 => KnownFullCodes,
            _ => null,
        };
        return table is not null && table.TryGetValue(trimmed, out var name) ? name : trimmed;
    }
}
