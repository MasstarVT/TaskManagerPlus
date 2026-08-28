using System.Globalization;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, items 29/30/32/33/35/37: shared hex-string parsing for a bugcheck code/parameter
/// value. Two different upstream sources format the same underlying numeric value with different
/// minimum digit widths - EventLogService.FormatBugcheckValue's "0x" + (at least) 8 hex digits,
/// and MinidumpParserService's "0x" + (at least) 16 hex digits for the binary-parsed dump path -
/// so every decoder in this chunk goes through this one lenient parse rather than each assuming
/// a fixed width.
/// </summary>
public static class BugcheckHex
{
    /// <summary>Parses a "0x...." (or bare hex) string to its full 64-bit numeric value.</summary>
    public static bool TryParse(string? hex, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string s = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Same, truncated to the low 32 bits - what every bugcheck *code* (as opposed to a
    /// parameter, which can be a genuine 64-bit address) actually is.</summary>
    public static bool TryParseCode(string? hex, out uint value)
    {
        value = 0;
        if (!TryParse(hex, out var v)) return false;
        value = unchecked((uint)(v & 0xFFFFFFFF));
        return true;
    }
}
