using System.Buffers.Binary;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #487/#489/#490: decodes the raw UEFI Common Platform Error Record (CPER, UEFI Specification
/// Appendix N) binary blob that a Microsoft-Windows-WHEA-Logger event carries as its own "RawData"
/// EventData field - the same record Windows' WHEA subsystem builds from a firmware- or CPU-
/// reported hardware error, and the same format Linux's APEI/GHES error handling reads for the
/// exact same class of hardware. Unlike most of this app's other event-log reads, this is a real,
/// versioned, cross-platform binary specification (documented at uefi.org, not a Windows-internal
/// undocumented layout) - so this decoder reads fields directly off the wire rather than scraping
/// message text.
///
/// Every multi-byte read here is bounds-checked against the buffer length before being touched, and
/// any GUID/length/count that doesn't look sane aborts just that field or section rather than
/// guessing - a decode gap always shows up as a missing/null field on the way out, never a
/// fabricated one. The processor machine-check bank decode additionally prefers a raw captured
/// register value (copied verbatim from the CPU by the record itself) over this app's own bit
/// interpretation wherever one was captured, to stay as close to "what the hardware reported" as
/// possible - see TryDecodeProcessorIa's remarks.
/// </summary>
public static class CperDecoder
{
    private const int HeaderLength = 128;
    private const int SectionDescriptorLength = 72;
    private static readonly byte[] Signature = { (byte)'C', (byte)'P', (byte)'E', (byte)'R' };

    // Record-level "notification type" GUID (UEFI CPER header, offset 80) - identifies which WHEA
    // error source produced the record.
    private static readonly Guid NotifyNmi = new("5BAD89FF-B7E6-42C9-814A-CF2485D6E98A");

    // Section-type GUIDs (UEFI CPER section descriptor, offset 16 within each descriptor) - what
    // kind of error data one section's body holds.
    private static readonly Guid SecProcGeneric = new("9876CCAD-47B4-4BDB-B65E-16F193C4F3DB");
    private static readonly Guid SecProcIa = new("DC3EA0B0-A144-4797-B95B-53FA242B6E1D");
    private static readonly Guid SecPlatformMem = new("A5BC1114-6F64-4EDE-B863-3E83ED7C83B1");
    private static readonly Guid SecPcie = new("D995E954-BBC1-430F-AD91-B44DCB3C6F35");
    private static readonly Guid SecPciXBus = new("C5753963-3B84-4095-BF78-EDDAD3F9C9DD");

    // IA32/X64 Processor Error Information Structure "err_type" GUIDs - which of the four check-
    // info bit layouts (Cache/TLB/Bus/MS) applies to one cper_ia_err_info entry. Only MS (the
    // microarchitecture-specific check, closest to a plain machine-check bank) is decoded below;
    // Cache/TLB/Bus entries are recognized but not decoded field-by-field.
    private static readonly Guid ErrTypeMs = new("48AB7F57-DC34-4F6C-A7D3-B0B5B0A74314");

    public static CperRecord? Decode(byte[]? raw)
    {
        if (raw is null || raw.Length < HeaderLength) return null;
        try
        {
            if (!raw.AsSpan(0, 4).SequenceEqual(Signature)) return null;

            uint severityCode = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(12, 4));
            ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(10, 2));
            var notifyType = ReadGuid(raw, 80);
            if (notifyType is null) return null;

            var sections = new List<CperSection>();
            for (int i = 0; i < sectionCount; i++)
            {
                int descOffset = HeaderLength + i * SectionDescriptorLength;
                if (descOffset + SectionDescriptorLength > raw.Length) break;

                uint sectionOffset = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(descOffset, 4));
                uint sectionLength = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(descOffset + 4, 4));
                var sectionType = ReadGuid(raw, descOffset + 16);
                if (sectionType is null) continue;
                if (sectionOffset >= raw.Length || sectionLength == 0 || sectionOffset + sectionLength > (uint)raw.Length) continue;

                var section = new CperSection { SectionType = sectionType.Value };
                if (sectionType == SecProcIa)
                    section.ProcessorIa = TryDecodeProcessorIa(raw, (int)sectionOffset, (int)sectionLength);
                else if (sectionType == SecPcie)
                    section.Pcie = TryDecodePcie(raw, (int)sectionOffset, (int)sectionLength);

                sections.Add(section);
            }

            var sourceType = ClassifySourceType(sections, notifyType.Value);
            var severity = ClassifySeverity(severityCode);

            return new CperRecord { Severity = severity, SourceType = sourceType, Sections = sections };
        }
        catch
        {
            // Any bounds/format surprise this decoder didn't anticipate - degrade to "couldn't
            // decode the binary record" rather than propagate a parse exception or hand back
            // partial, possibly-misread data.
            return null;
        }
    }

    private static WheaErrorSourceType ClassifySourceType(List<CperSection> sections, Guid notifyType)
    {
        if (sections.Any(s => s.SectionType == SecProcIa || s.SectionType == SecProcGeneric))
            return WheaErrorSourceType.MachineCheck;
        if (sections.Any(s => s.SectionType == SecPcie || s.SectionType == SecPciXBus))
            return WheaErrorSourceType.PciExpress;
        if (sections.Any(s => s.SectionType == SecPlatformMem))
            return WheaErrorSourceType.PlatformMemory;
        if (notifyType == NotifyNmi) return WheaErrorSourceType.Nmi;
        return WheaErrorSourceType.Other;
    }

    // UEFI CPER Appendix N.2.1 ErrorSeverity: 0=Recoverable, 1=Fatal, 2=Corrected, 3=Informational.
    private static WheaErrorSeverity ClassifySeverity(uint code) => code switch
    {
        0 => WheaErrorSeverity.Recoverable,
        1 => WheaErrorSeverity.Fatal,
        2 => WheaErrorSeverity.Corrected,
        3 => WheaErrorSeverity.Informational,
        _ => WheaErrorSeverity.Unknown,
    };

    private static Guid? ReadGuid(byte[] buf, int offset)
    {
        if (offset < 0 || offset + 16 > buf.Length) return null;
        return new Guid(buf.AsSpan(offset, 16));
    }

    // ------------------------------------------------------------------------------------------
    // #490: IA32/X64 Processor Error Section (struct cper_sec_proc_ia) - a 64-byte fixed header
    // (validation_bits, lapic_id, cpuid[48]) followed by ErrInfoCount cper_ia_err_info entries (64
    // bytes each) and then CtxInfoCount cper_ia_proc_ctx entries (a 16-byte header plus a raw
    // register-array tail, each entry padded to a 16-byte multiple). Both counts are packed into
    // validation_bits: bits 7:2 = error-info count, bits 13:8 = context-info count.
    // ------------------------------------------------------------------------------------------

    private const int ProcIaHeaderLength = 64;
    private const int ErrInfoEntryLength = 64;
    private const int ProcCtxHeaderLength = 16;

    /// <summary>
    /// Bank number and the raw MCi_STATUS value come from a captured processor-context register
    /// array when the record includes one (WHEA_XPF_CONTEXT_INFO / cper_ia_proc_ctx - an MSR
    /// context entry is documented to carry a nonzero MSRAddress; IA32_MCi_STATUS = 0x401 + 4*bank
    /// is stable IA32 architecture, unchanged across vendors/generations for over two decades) -
    /// this is a literal copy of the hardware's own register, not a re-interpretation. When no such
    /// register was captured, this falls back to the CPER-native "MS Check" check-info bits (UEFI
    /// spec Appendix N), which only cover Uncorrected/ProcessorContextCorrupt/Overflow - Bank and
    /// RawMciStatus stay null in that case rather than guessed.
    /// </summary>
    private static CperProcessorIa? TryDecodeProcessorIa(byte[] raw, int offset, int length)
    {
        if (length < ProcIaHeaderLength || offset + ProcIaHeaderLength > raw.Length) return null;

        ulong validationBits = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(offset, 8));
        var result = new CperProcessorIa();
        if ((validationBits & 0x1) != 0) // VALID_LAPIC_ID
            result.LocalApicId = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(offset + 8, 8));

        int errInfoCount = (int)((validationBits >> 2) & 0x3F);  // bits 7:2
        int ctxInfoCount = (int)((validationBits >> 8) & 0x3F);  // bits 13:8
        int sectionEnd = offset + length;

        int cursor = offset + ProcIaHeaderLength;
        var msCheckInfos = new List<ulong>();
        for (int i = 0; i < errInfoCount; i++)
        {
            if (cursor + ErrInfoEntryLength > sectionEnd || cursor + ErrInfoEntryLength > raw.Length) break;

            var errType = ReadGuid(raw, cursor);
            ulong infoValidBits = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(cursor + 16, 8));
            ulong checkInfo = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(cursor + 24, 8));
            if (errType == ErrTypeMs && (infoValidBits & 0x1) != 0) // INFO_VALID_CHECK_INFO
                msCheckInfos.Add(checkInfo);

            cursor += ErrInfoEntryLength;
        }

        var banks = new List<CperMcaBank>();
        for (int i = 0; i < ctxInfoCount; i++)
        {
            if (cursor + ProcCtxHeaderLength > sectionEnd || cursor + ProcCtxHeaderLength > raw.Length) break;

            ushort regArrSize = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(cursor + 2, 2));
            uint msrAddr = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(cursor + 4, 4));
            int dataOffset = cursor + ProcCtxHeaderLength;

            // IA32_MCi_STATUS = 0x401 + 4*bank (Intel SDM Vol.3B / AMD BKDG, stable across x86
            // implementations) - MSRAddress is documented to be meaningful only for an MSR-typed
            // context entry (zero otherwise), so this pattern match is a safe, self-validating gate
            // regardless of the exact numeric RegisterContextType tag.
            if (msrAddr is >= 0x400 and <= 0x4FF && (msrAddr & 0x3) == 1 && regArrSize >= 8 &&
                dataOffset + 8 <= raw.Length && dataOffset + 8 <= sectionEnd)
            {
                ulong mciStatus = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(dataOffset, 8));
                int bank = (int)((msrAddr - 0x401) / 4);
                banks.Add(new CperMcaBank
                {
                    BankNumber = bank,
                    RawMciStatus = mciStatus,
                    // IA32_MCi_STATUS architectural top bits - stable across vendors/generations,
                    // unlike the model-specific error-code bits (15:0/31:16) deliberately left
                    // undecoded below.
                    Overflow = (mciStatus & (1UL << 62)) != 0,
                    Uncorrected = (mciStatus & (1UL << 61)) != 0,
                    ProcessorContextCorrupt = (mciStatus & (1UL << 57)) != 0,
                });
            }

            int entryLen = ProcCtxHeaderLength + regArrSize;
            entryLen = (entryLen + 15) / 16 * 16; // each context entry is padded to a 16-byte multiple
            if (entryLen <= 0) break;
            cursor += entryLen;
        }

        // No raw MSR register captured for this record - fall back to the CPER-native "MS Check"
        // structure's own check-info flags (UEFI spec Appendix N: bits 19/20/23 of a 64-bit field
        // starting with a 16-bit ValidFields mask, then a 3-bit ErrorType). No bank number or raw
        // status value is available this way - only the three flags below.
        if (banks.Count == 0)
        {
            foreach (var checkInfo in msCheckInfos)
            {
                banks.Add(new CperMcaBank
                {
                    ProcessorContextCorrupt = (checkInfo & (1UL << 19)) != 0,
                    Uncorrected = (checkInfo & (1UL << 20)) != 0,
                    Overflow = (checkInfo & (1UL << 23)) != 0,
                });
            }
        }

        result.Banks = banks;
        return result.LocalApicId is null && banks.Count == 0 ? null : result;
    }

    // ------------------------------------------------------------------------------------------
    // #489: PCI Express Error Section (struct cper_sec_pcie, UEFI spec Appendix N) - a fixed 208-
    // byte layout: validation_bits(8) @0, port_type(4) @8, version(4) @12, command/status(4) @16,
    // reserved(4) @20, device_id(16) @24, serial_number(8) @40, bridge(4) @48, capability[60] @52,
    // aer_info[96] @112. aer_info mirrors the PCI Express AER Extended Capability register block
    // verbatim (starting with that capability's own 4-byte header, ID 0x0001) - validated by
    // checking that ID before trusting the status DWORDs that follow it.
    // ------------------------------------------------------------------------------------------

    private const int PcieDeviceIdOffset = 24;
    private const int PcieAerInfoOffset = 112;

    private static CperPcie? TryDecodePcie(byte[] raw, int offset, int length)
    {
        if (length < PcieDeviceIdOffset + 16 || offset + PcieDeviceIdOffset + 16 > raw.Length) return null;

        // device_id sub-structure (16 bytes): vendor_id u16 @0, device_id u16 @2, class_code[3] @4,
        // function u8 @7, device u8 @8, segment u16 @9, bus u8 @11, secondary_bus u8 @12, slot u16 @13.
        int d = offset + PcieDeviceIdOffset;
        ushort vendorId = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(d, 2));
        ushort deviceIdVal = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(d + 2, 2));
        byte function = raw[d + 7];
        byte device = raw[d + 8];
        ushort segment = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(d + 9, 2));
        byte bus = raw[d + 11];

        var result = new CperPcie
        {
            Segment = segment,
            Bus = bus,
            Device = device,
            Function = function,
            VendorId = vendorId == 0xFFFF ? null : vendorId,
            DeviceId = deviceIdVal == 0xFFFF ? null : deviceIdVal,
        };

        int aerOffset = offset + PcieAerInfoOffset;
        if (aerOffset + 24 <= raw.Length && aerOffset + 24 <= offset + length)
        {
            // AER Extended Capability register block: header(4) @0, UncorrectableErrorStatus(4) @4,
            // UncorrectableErrorMask(4) @8, UncorrectableErrorSeverity(4) @12,
            // CorrectableErrorStatus(4) @16, CorrectableErrorMask(4) @20, ...
            ushort capId = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(aerOffset, 2));
            if (capId == 0x0001)
            {
                result.UncorrectableStatus = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(aerOffset + 4, 4));
                result.CorrectableStatus = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(aerOffset + 16, 4));
            }
        }

        return result;
    }
}

/// <summary>Decoded UEFI CPER record - see CperDecoder's remarks.</summary>
public sealed class CperRecord
{
    public WheaErrorSeverity Severity { get; init; } = WheaErrorSeverity.Unknown;
    public WheaErrorSourceType SourceType { get; init; } = WheaErrorSourceType.Unknown;
    public List<CperSection> Sections { get; init; } = new();
}

public sealed class CperSection
{
    public Guid SectionType { get; init; }
    public CperProcessorIa? ProcessorIa { get; set; }
    public CperPcie? Pcie { get; set; }
}

public sealed class CperProcessorIa
{
    public ulong? LocalApicId { get; set; }
    public List<CperMcaBank> Banks { get; set; } = new();
}

/// <summary>One decoded machine-check bank - see CperDecoder.TryDecodeProcessorIa's remarks for
/// which fields come from a raw captured register vs. the CPER-native check-info fallback.</summary>
public sealed class CperMcaBank
{
    public int? BankNumber { get; init; }
    public ulong? RawMciStatus { get; init; }
    public bool? Overflow { get; init; }
    public bool? Uncorrected { get; init; }
    public bool? ProcessorContextCorrupt { get; init; }
}

/// <summary>Raw fields extracted from a PCI Express Error Section - EventLogService maps this into
/// the presentation-level Models.PcieAerDetail (adding the friendly-device-name lookup and decoded
/// status-flag text).</summary>
public sealed class CperPcie
{
    public int Segment { get; init; }
    public int Bus { get; init; }
    public int Device { get; init; }
    public int Function { get; init; }
    public int? VendorId { get; init; }
    public int? DeviceId { get; init; }
    public uint? UncorrectableStatus { get; set; }
    public uint? CorrectableStatus { get; set; }
}
