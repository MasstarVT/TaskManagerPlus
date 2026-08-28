using System.Runtime.InteropServices;
using System.Text;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #440: reads the raw SMBIOS firmware table via GetSystemFirmwareTable('RSMB') and decodes
/// Memory Device (Type 17) structures for the fields WMI's Win32_PhysicalMemory simply doesn't
/// expose - part number, serial number, rank, configured/min/max voltage, form factor, bank
/// locator, memory technology. There is no WMI class or documented tool that surfaces these (the
/// closest, Win32_PhysicalMemory, only carries DeviceLocator/Capacity/Speed/ConfiguredClockSpeed/
/// Manufacturer/SMBIOSMemoryType - see SystemSpecsService.ReadMemoryModules), so this is the raw-
/// interop exception CLAUDE.md documents rather than the app's usual "known tool/WMI class first"
/// rule. Every failure mode (the firmware table call itself failing, a truncated/malformed table,
/// a structure shorter than a given field's offset because the running BIOS predates that SMBIOS
/// revision) degrades to an empty result or a null field - never a fabricated value. Matched back
/// to WMI's per-module list by DeviceLocator in SystemSpecsService.
/// </summary>
public static class SmbiosMemoryService
{
    // The documented FirmwareTableProviderSignature for SMBIOS data, i.e. the C multi-char literal
    // 'RSMB' as MSVC packs it (('R'&lt;&lt;24)|('S'&lt;&lt;16)|('M'&lt;&lt;8)|'B') - not the
    // MAKE_SIGNATURE byte-order macro some interop samples confuse it with.
    private const uint RsmbSignature = 0x52534D42;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableId,
        IntPtr firmwareTableBuffer, uint bufferSize);

    /// <summary>Reads and decodes every Type 17 (Memory Device) structure in the current firmware's
    /// SMBIOS table. Returns an empty list - never throws - on any failure: the API call itself
    /// failing (some virtualized/embedded firmware doesn't implement the SMBIOS provider), a zero-
    /// or garbage-length table, or a parse error partway through (a malformed structure just stops
    /// the walk there, keeping whatever valid structures were already decoded).</summary>
    public static List<SmbiosMemoryDevice> ReadMemoryDevices()
    {
        try
        {
            uint size = GetSystemFirmwareTable(RsmbSignature, 0, IntPtr.Zero, 0);
            if (size == 0 || size > 4 * 1024 * 1024) return new(); // sanity cap - a real SMBIOS table is a few KB

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint written = GetSystemFirmwareTable(RsmbSignature, 0, buffer, size);
                if (written == 0 || written > size) return new();

                byte[] raw = new byte[written];
                Marshal.Copy(buffer, raw, 0, (int)written);
                return ParseTable(raw);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            // Any interop/marshaling failure - "no SMBIOS data available", the rest of this app's
            // memory reporting still works from WMI alone.
            return new();
        }
    }

    /// <summary>
    /// Walks the RawSMBIOSData buffer: an 8-byte header (Used20CallingMethod, SMBIOSMajorVersion,
    /// SMBIOSMinorVersion, DmiRevision, then a little-endian DWORD Length) followed by Length bytes
    /// of back-to-back structures. Each structure is {Type byte, formatted-Length byte, Handle
    /// WORD, ...formatted fields..., string-set}, where the string-set is a sequence of null-
    /// terminated ANSI strings ending in an extra null byte (a double-null overall) - found here by
    /// scanning forward from the end of the formatted section for the first two consecutive zero
    /// bytes, the same approach dmidecode's own struct walker uses, rather than a stateful per-
    /// string loop that has to special-case "zero strings" separately.
    /// </summary>
    private static List<SmbiosMemoryDevice> ParseTable(byte[] raw)
    {
        var result = new List<SmbiosMemoryDevice>();
        if (raw.Length < 8) return result;

        uint tableLen = BitConverter.ToUInt32(raw, 4);
        int start = 8;
        int end = (int)Math.Min((long)raw.Length, (long)start + tableLen);

        int offset = start;
        while (offset + 4 <= end)
        {
            byte type = raw[offset];
            byte length = raw[offset + 1];
            if (length < 4 || offset + length > end) break; // malformed/truncated - stop here

            int formattedEnd = offset + length;

            // Scan for the double-null that ends this structure's string-set. next stays at
            // formattedEnd (then +2) when there are no strings at all - a bare double-null right
            // after the formatted section is a valid, common case (most structures reference at
            // least one string, but not all do).
            int next = formattedEnd;
            while (next + 1 < end && !(raw[next] == 0 && raw[next + 1] == 0)) next++;
            bool foundTerminator = next + 1 < end;
            int stringsEnd = next;

            if (type == 17)
            {
                var strings = SplitStrings(raw, formattedEnd, stringsEnd);
                result.Add(ParseType17(raw, offset, length, strings));
            }
            else if (type == 127)
            {
                break; // End-Of-Table marker
            }

            if (!foundTerminator) break; // ran off the end without finding a terminator - malformed
            offset = next + 2;
        }
        return result;
    }

    private static List<string> SplitStrings(byte[] raw, int start, int end)
    {
        var list = new List<string>();
        int pos = start;
        while (pos < end)
        {
            int strStart = pos;
            while (pos < end && raw[pos] != 0) pos++;
            list.Add(Encoding.ASCII.GetString(raw, strStart, pos - strStart));
            pos++; // skip the null terminator
        }
        return list;
    }

    /// <summary>SMBIOS string numbers are 1-based; 0 (or out of range) means "no string" - GetString
    /// returns empty rather than throwing, letting every caller just check for an empty result.</summary>
    private static string GetString(List<string> strings, int number)
    {
        if (number <= 0 || number > strings.Count) return string.Empty;
        return strings[number - 1].Trim();
    }

    private static SmbiosMemoryDevice ParseType17(byte[] raw, int offset, byte length, List<string> strings)
    {
        // Every read below is bounds-checked against `length` (this structure's own declared
        // formatted-section length) before touching the buffer - a BIOS predating a given SMBIOS
        // revision reports a correspondingly shorter Length, and that field simply stays
        // null/"Unknown" rather than reading past the structure into the next one's bytes.
        bool Has(int fieldEndOffset) => length >= fieldEndOffset;
        byte U8(int o) => raw[offset + o];
        ushort U16(int o) => BitConverter.ToUInt16(raw, offset + o);
        uint U32(int o) => BitConverter.ToUInt32(raw, offset + o);

        int? totalWidth = null, dataWidth = null;
        if (Has(0x0A)) { ushort v = U16(0x08); if (v != 0xFFFF) totalWidth = v; }
        if (Has(0x0C)) { ushort v = U16(0x0A); if (v != 0xFFFF) dataWidth = v; }

        long sizeBytes = 0;
        if (Has(0x0E))
        {
            ushort sizeRaw = U16(0x0C);
            if (sizeRaw == 0xFFFF)
            {
                // Unknown size - leave at 0 (SystemSpecsService already treats 0 as "unknown"
                // for the WMI-sourced figure it prefers to display anyway).
            }
            else if (sizeRaw == 0x7FFF && Has(0x20))
            {
                uint extMb = U32(0x1C) & 0x7FFFFFFF;
                sizeBytes = (long)extMb * 1024L * 1024L;
            }
            else if (sizeRaw != 0)
            {
                bool isKb = (sizeRaw & 0x8000) != 0;
                int units = sizeRaw & 0x7FFF;
                sizeBytes = isKb ? (long)units * 1024L : (long)units * 1024L * 1024L;
            }
        }

        string formFactor = Has(0x0F) ? FormFactorName(U8(0x0E)) : "Unknown";
        string deviceLocator = Has(0x11) ? GetString(strings, U8(0x10)) : string.Empty;
        string bankLocator = Has(0x12) ? GetString(strings, U8(0x11)) : string.Empty;
        string memoryType = Has(0x13) ? MemoryTypeName(U8(0x12)) : "Unknown";

        int? ratedSpeed = null;
        if (Has(0x17)) { ushort v = U16(0x15); if (v != 0 && v != 0xFFFF) ratedSpeed = v; }

        string manufacturer = Has(0x18) ? GetString(strings, U8(0x17)) : string.Empty;
        string serialNumber = Has(0x19) ? GetString(strings, U8(0x18)) : string.Empty;
        string assetTag = Has(0x1A) ? GetString(strings, U8(0x19)) : string.Empty;
        string partNumber = Has(0x1B) ? GetString(strings, U8(0x1A)) : string.Empty;

        int? rank = null;
        if (Has(0x1C)) { int r = U8(0x1B) & 0x0F; if (r > 0) rank = r; }

        int? configuredSpeed = null;
        if (Has(0x22)) { ushort v = U16(0x20); if (v != 0 && v != 0xFFFF) configuredSpeed = v; }

        double? minV = null, maxV = null, confV = null;
        if (Has(0x24)) { ushort v = U16(0x22); if (v != 0) minV = v / 1000.0; }
        if (Has(0x26)) { ushort v = U16(0x24); if (v != 0) maxV = v / 1000.0; }
        if (Has(0x28)) { ushort v = U16(0x26); if (v != 0) confV = v / 1000.0; }

        string memoryTechnology = Has(0x29) ? MemoryTechnologyName(U8(0x28)) : "Unknown";

        return new SmbiosMemoryDevice
        {
            DeviceLocator = deviceLocator,
            BankLocator = bankLocator,
            Manufacturer = manufacturer,
            SerialNumber = serialNumber,
            PartNumber = partNumber,
            AssetTag = assetTag,
            TotalWidthBits = totalWidth,
            DataWidthBits = dataWidth,
            SizeBytes = sizeBytes,
            FormFactor = formFactor,
            MemoryType = memoryType,
            RatedSpeedMts = ratedSpeed,
            ConfiguredSpeedMts = configuredSpeed,
            RankCount = rank,
            MinVoltageV = minV,
            MaxVoltageV = maxV,
            ConfiguredVoltageV = confV,
            MemoryTechnology = memoryTechnology,
        };
    }

    // SMBIOS spec (DMTF), Type 17 offset 0x0E - a curated subset of the documented enum covering
    // every form factor a consumer desktop/laptop DIMM slot actually reports; unlisted/reserved
    // codes fall back to "Unknown" rather than a guess, the same tier as ChassisTypeName/
    // VideoOutputTechnologyName elsewhere in this file's sibling service.
    private static string FormFactorName(int code) => code switch
    {
        0x01 => "Other",
        0x03 => "SIMM",
        0x05 => "Chip",
        0x06 => "DIP",
        0x08 => "Proprietary card",
        0x09 => "DIMM",
        0x0A => "TSOP",
        0x0C => "RIMM",
        0x0D => "SODIMM",
        0x0E => "SRIMM",
        0x0F => "FB-DIMM",
        0x10 => "Die",
        _ => "Unknown",
    };

    // Type 17 offset 0x12 - the raw SMBIOS "Memory Type" enum (DMTF spec Table 80), the same code
    // space Win32_PhysicalMemory.SMBIOSMemoryType passes straight through (see
    // SystemSpecsService.DdrGenerationName), just with the full documented enum rather than only
    // the DDR generations that method cares about, since this label also needs to cover non-DDR
    // module types honestly rather than always saying "Unknown" for them.
    private static string MemoryTypeName(int code) => code switch
    {
        0x01 => "Other",
        0x03 => "DRAM",
        0x06 => "SRAM",
        0x12 => "DDR",
        0x13 => "DDR2",
        0x14 => "DDR2 FB-DIMM",
        0x18 => "DDR3",
        0x19 => "FBD2",
        0x1A => "DDR4",
        0x1B => "LPDDR",
        0x1C => "LPDDR2",
        0x1D => "LPDDR3",
        0x1E => "LPDDR4",
        0x1F => "Logical non-volatile device",
        0x20 => "HBM",
        0x21 => "HBM2",
        0x22 => "DDR5",
        0x23 => "LPDDR5",
        0x24 => "HBM3",
        _ => "Unknown",
    };

    // Type 17 offset 0x28, SMBIOS 3.2+ only - absent (and reported as "Unknown", not this table)
    // on the large majority of BIOS revisions still in the field.
    private static string MemoryTechnologyName(int code) => code switch
    {
        0x01 => "Other",
        0x03 => "DRAM",
        0x04 => "NVDIMM-N",
        0x05 => "NVDIMM-F",
        0x06 => "NVDIMM-P",
        0x07 => "Intel persistent memory",
        _ => "Unknown",
    };
}
