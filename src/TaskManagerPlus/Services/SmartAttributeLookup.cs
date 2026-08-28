namespace TaskManagerPlus.Services;

/// <summary>
/// #303: attribute-ID-to-friendly-name lookup for the ~60 SMART attribute IDs a home/desktop user
/// is actually likely to see, in the same spirit as BugcheckCodeLookup and JedecManufacturerLookup
/// - a small, deliberately non-exhaustive table (not a full vendor spec-sheet database), so a raw
/// SMART grid row reads "05 - Reallocated Sector Count" instead of a bare hex ID. Several IDs are
/// vendor-reassigned in practice (0xAD/0xB1/0xE8/0xE9/0xF1/0xF2 mean different things per vendor -
/// see SmartVendorProfiles for the per-vendor overrides that take priority over this generic
/// table); the names here are the most common/ATA-8-documented meaning, used only when no vendor
/// profile matched or the matched profile doesn't override that particular ID.
/// </summary>
public static class SmartAttributeLookup
{
    private static readonly Dictionary<byte, string> Names = new()
    {
        [0x01] = "Read Error Rate",
        [0x02] = "Throughput Performance",
        [0x03] = "Spin-Up Time",
        [0x04] = "Start/Stop Count",
        [0x05] = "Reallocated Sector Count",
        [0x07] = "Seek Error Rate",
        [0x08] = "Seek Time Performance",
        [0x09] = "Power-On Hours",
        [0x0A] = "Spin Retry Count",
        [0x0B] = "Recalibration Retries",
        [0x0C] = "Power Cycle Count",
        [0x0D] = "Soft Read Error Rate",
        [0x16] = "Current Helium Level",
        [0xAA] = "Available Reserved Space",
        [0xAB] = "SSD Program Fail Count",
        [0xAC] = "SSD Erase Fail Count",
        [0xAD] = "Wear Leveling Count",
        [0xAE] = "Unexpected Power Loss Count",
        [0xAF] = "Power Loss Protection Failure",
        [0xB0] = "Erase Fail Count",
        [0xB1] = "Wear Range Delta",
        [0xB2] = "Used Reserved Block Count",
        [0xB3] = "Used Reserved Block Count Total",
        [0xB4] = "Unused Reserved Block Count Total",
        [0xB5] = "Program Fail Count Total",
        [0xB6] = "Erase Fail Count Total",
        [0xB7] = "SATA Downshift Error Count",
        [0xB8] = "End-to-End Error Count",
        [0xBB] = "Reported Uncorrectable Errors",
        [0xBC] = "Command Timeout",
        [0xBD] = "High Fly Writes",
        [0xBE] = "Airflow Temperature",
        [0xBF] = "G-Sense Error Rate",
        [0xC0] = "Power-Off Retract Count",
        [0xC1] = "Load Cycle Count",
        [0xC2] = "Temperature",
        [0xC3] = "Hardware ECC Recovered",
        [0xC4] = "Reallocation Event Count",
        [0xC5] = "Current Pending Sector Count",
        [0xC6] = "Offline Uncorrectable Sector Count",
        [0xC7] = "UDMA CRC Error Count",
        [0xC8] = "Multi-Zone Error Rate",
        [0xC9] = "Soft Read Error Rate",
        [0xCA] = "Data Address Mark Errors",
        [0xCB] = "Run Out Cancel",
        [0xCC] = "Soft ECC Correction",
        [0xCD] = "Thermal Asperity Rate",
        [0xCE] = "Flying Height",
        [0xCF] = "Spin High Current",
        [0xD0] = "Spin Buzz",
        [0xD1] = "Offline Seek Performance",
        [0xD3] = "Vibration During Write",
        [0xD4] = "Shock During Write",
        [0xDC] = "Disk Shift",
        [0xDD] = "G-Sense Error Rate (alt.)",
        [0xDE] = "Loaded Hours",
        [0xDF] = "Load/Unload Retry Count",
        [0xE0] = "Load Friction",
        [0xE1] = "Load/Unload Cycle Count",
        [0xE2] = "Load 'In'-Time / Timed Workload Media Wear",
        [0xE3] = "Torque Amplification Count",
        [0xE4] = "Power-Off Retract Cycle",
        [0xE6] = "GMR Head Amplitude / Drive Life Protection Status",
        [0xE7] = "SSD Life Left",
        [0xE8] = "Endurance Remaining / Available Reserved Space",
        [0xE9] = "Media Wearout Indicator",
        [0xEA] = "Average Erase Count / Remaining Lifetime Percentage",
        [0xEB] = "Good Block Count / POR Recovery Count",
        [0xF0] = "Head Flying Hours",
        [0xF1] = "Total LBAs Written",
        [0xF2] = "Total LBAs Read",
        [0xF3] = "Total LBAs Written (Expanded)",
        [0xF4] = "Total LBAs Read (Expanded)",
        [0xF5] = "Cumulative Host Sectors Written",
        [0xF6] = "Minimum Spares Remaining",
        [0xF7] = "Newly Added Bad Flash Block",
        [0xF8] = "Free Fall Protection (alt.)",
        [0xF9] = "NAND Writes (1 GiB units)",
        [0xFA] = "Read Error Retry Rate",
        [0xFB] = "Minimum Spares Remaining (alt.)",
        [0xFC] = "Newly Added Bad Flash Block (alt.)",
        [0xFE] = "Free Fall Protection",
    };

    /// <summary>Resolves an attribute ID to a friendly name, falling back to a bare hex label
    /// ("0xNN") for anything outside this deliberately non-exhaustive table - never a guess.</summary>
    public static string Resolve(byte id) => Names.TryGetValue(id, out var name) ? name : $"Vendor-specific (0x{id:X2})";
}
