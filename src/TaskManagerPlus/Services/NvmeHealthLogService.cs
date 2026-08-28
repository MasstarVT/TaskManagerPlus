using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 13, #313-#322: NVMe-specific health/log-page/identify data, read via
/// IOCTL_STORAGE_QUERY_PROPERTY with PropertyId=StorageDeviceProtocolSpecificProperty,
/// ProtocolType=ProtocolTypeNvme - the documented Windows path for NVMe log pages and Identify,
/// no vendor driver required (this is the same tier CLAUDE.md calls "a known Windows tool/API",
/// not risky raw interop, even though it's P/Invoke - IOCTL_SCSI_PASS_THROUGH in
/// SmartRawAttributeService is the actual "no alternative exists" case).
///
/// #313's health-log-page-0x02 read (ReadHealthLog) is the foundational query nearly everything
/// else in this file decodes fields out of; #320 (error log, page 0x01), #321 (self-test log,
/// page 0x06) and #322 (Identify Controller, plus a best-effort Get Features follow-up for APST)
/// are each a separate log-page/Identify round trip, so they get their own on-demand method/button
/// rather than being bundled into the single ReadHealthLog call.
///
/// Every method here degrades to Unavailable+a stated reason (never a guessed/zeroed value) on any
/// failure - wrong disk, non-NVMe device, unsupported log page, access denied, or a controller that
/// simply doesn't answer this IOCTL - exactly the same tier as SmartRawAttributeService's WMI/SCSI
/// pass-through fallbacks.
/// </summary>
public static class NvmeHealthLogService
{
    // --- Win32 plumbing --------------------------------------------------------------------

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;

    // CTL_CODE(FILE_DEVICE_MASS_STORAGE=0x2d, 0x0500, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    private const uint StorageDeviceProtocolSpecificProperty = 50;
    private const uint PropertyStandardQuery = 0;
    private const uint ProtocolTypeNvme = 3;
    private const uint NVMeDataTypeIdentify = 1;
    private const uint NVMeDataTypeLogPage = 2;
    private const uint NVMeDataTypeFeature = 3;

    // sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA) - 10 DWORD fields.
    private const uint ProtocolSpecificDataSize = 40;
    private const uint QueryHeaderSize = 8; // STORAGE_PROPERTY_QUERY's PropertyId + QueryType

    private const uint HealthLogPageId = 0x02;
    private const uint ErrorLogPageId = 0x01;
    private const uint SelfTestLogPageId = 0x06;
    private const uint IdentifyCnsController = 1;
    private const uint ApstFeatureId = 0x0C;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string filename, uint access, uint share, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
        IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    private static T WithHandle<T>(int diskIndex, Func<SafeFileHandle, T> onOpen, Func<string, T> onFail)
    {
        string path = $@"\\.\PhysicalDrive{diskIndex}";
        SafeFileHandle? handle = null;
        try
        {
            handle = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                return onFail($"Could not open {path} (Win32 error {Marshal.GetLastWin32Error()}).");
            return onOpen(handle);
        }
        catch (Exception ex)
        {
            return onFail($"NVMe query threw: {ex.Message}");
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>Builds and sends one STORAGE_DEVICE_PROTOCOL_SPECIFIC_PROPERTY query for
    /// ProtocolTypeNvme, returning the `dataLength`-byte payload the controller placed after the
    /// echoed header, or null with a stated reason. `fixedReturnData` carries back the
    /// FixedProtocolReturnData dword (used by the Get Features/APST follow-up for the completion
    /// DW0, e.g. the APSTE enable bit).</summary>
    private static byte[]? QueryProtocolSpecific(SafeFileHandle handle, uint dataType, uint requestValue, uint requestSubValue, uint dataLength, out uint fixedReturnData, out string reason)
    {
        fixedReturnData = 0;
        reason = string.Empty;
        uint totalSize = QueryHeaderSize + ProtocolSpecificDataSize + dataLength;
        byte[] buffer = new byte[totalSize];

        // STORAGE_PROPERTY_QUERY
        BitConverter.GetBytes(StorageDeviceProtocolSpecificProperty).CopyTo(buffer, 0);
        BitConverter.GetBytes(PropertyStandardQuery).CopyTo(buffer, 4);
        // STORAGE_PROTOCOL_SPECIFIC_DATA, starting at offset 8 (STORAGE_PROPERTY_QUERY.AdditionalParameters)
        BitConverter.GetBytes(ProtocolTypeNvme).CopyTo(buffer, 8);
        BitConverter.GetBytes(dataType).CopyTo(buffer, 12);
        BitConverter.GetBytes(requestValue).CopyTo(buffer, 16);
        BitConverter.GetBytes(requestSubValue).CopyTo(buffer, 20);
        BitConverter.GetBytes(ProtocolSpecificDataSize).CopyTo(buffer, 24); // ProtocolDataOffset
        BitConverter.GetBytes(dataLength).CopyTo(buffer, 28);               // ProtocolDataLength

        GCHandle gc = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            IntPtr ptr = gc.AddrOfPinnedObject();
            bool ok = DeviceIoControl(handle, IoctlStorageQueryProperty, ptr, totalSize, ptr, totalSize, out uint bytesReturned, IntPtr.Zero);
            if (!ok)
            {
                reason = $"IOCTL_STORAGE_QUERY_PROPERTY failed (Win32 error {Marshal.GetLastWin32Error()}) - this controller may not support NVMe protocol-specific queries.";
                return null;
            }
            if (bytesReturned < QueryHeaderSize + ProtocolSpecificDataSize)
            {
                reason = "Device returned fewer bytes than the protocol-specific header requires.";
                return null;
            }

            fixedReturnData = BitConverter.ToUInt32(buffer, 32);
            int dataOffset = (int)(QueryHeaderSize + ProtocolSpecificDataSize);
            int available = Math.Max(0, (int)bytesReturned - dataOffset);
            var data = new byte[dataLength];
            Array.Copy(buffer, dataOffset, data, 0, Math.Min(available, (int)dataLength));
            return data;
        }
        finally
        {
            gc.Free();
        }
    }

    /// <summary>Reads a 128-bit little-endian counter as a BigInteger (never truncated to
    /// ulong/long - some of these fields are genuinely allowed to exceed 64 bits per spec, and
    /// silently truncating would be its own kind of fabrication).</summary>
    private static BigInteger ReadUInt128(byte[] data, int offset)
    {
        var bytes = new byte[17];
        Array.Copy(data, offset, bytes, 0, 16);
        // bytes[16] left 0 so the value is read as unsigned regardless of the top bit of byte 15.
        return new BigInteger(bytes);
    }

    private static string DecodeAscii(byte[] data, int offset, int length)
    {
        if (offset + length > data.Length) return string.Empty;
        return Encoding.ASCII.GetString(data, offset, length).Trim().TrimEnd('\0').Trim();
    }

    // --- #313/#314/#315/#316/#317/#318/#319: SMART/Health Information Log, page 0x02 -----------

    public static NvmeHealthLog ReadHealthLog(int diskIndex) => WithHandle(diskIndex, handle =>
    {
        var data = QueryProtocolSpecific(handle, NVMeDataTypeLogPage, HealthLogPageId, 0, 512, out _, out string reason);
        if (data is null) return new NvmeHealthLog { Available = false, UnavailableReason = reason };

        return new NvmeHealthLog
        {
            Available = true,
            RawBytes = data,
            CriticalWarningRaw = data[0],
            CompositeTemperatureKelvin = BitConverter.ToUInt16(data, 1),
            AvailableSparePercent = data[3],
            AvailableSpareThresholdPercent = data[4],
            PercentageUsed = data[5],
            DataUnitsRead = ReadUInt128(data, 32),
            DataUnitsWritten = ReadUInt128(data, 48),
            HostReadCommands = ReadUInt128(data, 64),
            HostWriteCommands = ReadUInt128(data, 80),
            ControllerBusyTimeMinutes = ReadUInt128(data, 96),
            PowerCycles = ReadUInt128(data, 112),
            PowerOnHours = ReadUInt128(data, 128),
            UnsafeShutdowns = ReadUInt128(data, 144),
            MediaAndDataIntegrityErrors = ReadUInt128(data, 160),
            ErrorInfoLogEntryCount = ReadUInt128(data, 176),
            WarningCompositeTempTimeMinutes = BitConverter.ToUInt32(data, 192),
            CriticalCompositeTempTimeMinutes = BitConverter.ToUInt32(data, 196),
            TemperatureSensorsKelvin = Enumerable.Range(0, 8).Select(i => BitConverter.ToUInt16(data, 200 + i * 2)).ToArray(),
            ThermalMgmtTemp1TransitionCount = BitConverter.ToUInt32(data, 216),
            ThermalMgmtTemp2TransitionCount = BitConverter.ToUInt32(data, 220),
            ThermalMgmtTemp1TotalTimeSeconds = BitConverter.ToUInt32(data, 224),
            ThermalMgmtTemp2TotalTimeSeconds = BitConverter.ToUInt32(data, 228),
        };
    }, reason => new NvmeHealthLog { Available = false, UnavailableReason = reason });

    // --- #320: Error Information Log, page 0x01 -------------------------------------------------

    /// <summary>64-byte entries, most-recent first; an all-zero ErrorCount marks an unused slot
    /// (an empty log is the normal, expected result on a healthy drive) and is skipped rather than
    /// shown as a spurious "error".</summary>
    public static (List<NvmeErrorLogEntry> Entries, bool Available, string Reason) ReadErrorLog(int diskIndex, int maxEntries = 64) => WithHandle(diskIndex, handle =>
    {
        uint dataLength = (uint)(maxEntries * 64);
        var data = QueryProtocolSpecific(handle, NVMeDataTypeLogPage, ErrorLogPageId, 0, dataLength, out _, out string reason);
        if (data is null) return (new List<NvmeErrorLogEntry>(), false, reason);

        var list = new List<NvmeErrorLogEntry>();
        for (int i = 0; i < maxEntries; i++)
        {
            int off = i * 64;
            if (off + 28 > data.Length) break;

            ulong errorCount = BitConverter.ToUInt64(data, off);
            if (errorCount == 0) continue; // unused slot

            ushort status = BitConverter.ToUInt16(data, off + 12);
            list.Add(new NvmeErrorLogEntry
            {
                ErrorCount = errorCount,
                SubmissionQueueId = BitConverter.ToUInt16(data, off + 8),
                CommandId = BitConverter.ToUInt16(data, off + 10),
                StatusField = status,
                StatusText = DecodeStatusField(status),
                Lba = BitConverter.ToUInt64(data, off + 16),
                NamespaceId = BitConverter.ToUInt32(data, off + 24),
            });
        }
        return (list, true, string.Empty);
    }, reason => (new List<NvmeErrorLogEntry>(), false, reason));

    /// <summary>Decodes the completion-queue Status Field's Status Code Type (SCT)/Status Code
    /// (SC) into text - phase tag (bit 0) dropped first, then SC = bits 8:1, SCT = bits 11:9, the
    /// standard NVMe completion status layout. Only the common generic-status codes are named;
    /// anything else is shown as its hex SC/SCT rather than guessed.</summary>
    private static string DecodeStatusField(ushort statusField)
    {
        int afterPhase = statusField >> 1;
        int sc = afterPhase & 0xFF;
        int sct = (afterPhase >> 8) & 0x7;

        string sctName = sct switch
        {
            0 => "Generic Command Status",
            1 => "Command Specific Status",
            2 => "Media and Data Integrity Errors",
            3 => "Path Related Status",
            7 => "Vendor Specific",
            _ => $"Reserved (SCT {sct})",
        };

        string scName = sct == 0 ? sc switch
        {
            0x00 => "Successful Completion",
            0x01 => "Invalid Command Opcode",
            0x02 => "Invalid Field in Command",
            0x03 => "Command ID Conflict",
            0x04 => "Data Transfer Error",
            0x05 => "Commands Aborted due to Power Loss Notification",
            0x06 => "Internal Error",
            0x07 => "Command Abort Requested",
            0x0A => "Invalid Namespace or Format",
            0x81 => "Conflicting Attributes",
            _ => $"SC 0x{sc:X2}",
        } : $"SC 0x{sc:X2}";

        return $"{sctName} - {scName}";
    }

    // --- #321: Device Self-test Log, page 0x06 --------------------------------------------------

    /// <summary>4-byte header (current op + completion %) followed by 20 fixed 28-byte result
    /// entries, most-recent first. A result-code nibble of 0xF marks an unused entry and is
    /// skipped.</summary>
    public static (List<NvmeSelfTestResult> Results, string CurrentOperationText, byte CurrentCompletionPercent, bool Available, string Reason) ReadSelfTestLog(int diskIndex) => WithHandle(diskIndex, handle =>
    {
        const uint dataLength = 4 + 20 * 28;
        var data = QueryProtocolSpecific(handle, NVMeDataTypeLogPage, SelfTestLogPageId, 0, dataLength, out _, out string reason);
        if (data is null) return (new List<NvmeSelfTestResult>(), string.Empty, (byte)0, false, reason);

        byte currentOp = data[0];
        byte currentCompletion = data[1];
        string currentOpText = currentOp switch
        {
            0 => "No self-test in progress",
            1 => "Short self-test in progress",
            2 => "Extended self-test in progress",
            0xE => "Vendor-specific self-test in progress",
            _ => $"Unknown (0x{currentOp:X2})",
        };

        var list = new List<NvmeSelfTestResult>();
        for (int i = 0; i < 20; i++)
        {
            int off = 4 + i * 28;
            if (off + 28 > data.Length) break;

            byte statusByte = data[off];
            byte resultCode = (byte)(statusByte & 0x0F);
            byte testCode = (byte)((statusByte >> 4) & 0x0F);
            if (resultCode == 0x0F) continue; // unused entry

            byte validBits = data[off + 2];
            ulong powerOnHours = BitConverter.ToUInt64(data, off + 4);
            uint nsid = BitConverter.ToUInt32(data, off + 16);
            ulong lba = BitConverter.ToUInt64(data, off + 20);

            list.Add(new NvmeSelfTestResult
            {
                OperationText = testCode switch
                {
                    1 => "Short",
                    2 => "Extended",
                    0xE => "Vendor-specific",
                    _ => $"Unknown (0x{testCode:X})",
                },
                StatusText = resultCode switch
                {
                    0 => "Completed without error",
                    1 => "Aborted by Device Self-test command",
                    2 => "Aborted by controller reset",
                    3 => "Aborted due to namespace removal",
                    4 => "Aborted due to Format NVM command",
                    5 => "Fatal or unknown test error",
                    6 => "Completed with segment failure",
                    7 => "Completed with segment failure and unknown segment",
                    8 => "Aborted for unknown reason",
                    9 => "Aborted due to sanitize operation",
                    _ => $"Unknown result (0x{resultCode:X})",
                },
                SegmentNumber = data[off + 1],
                PowerOnHours = (validBits != 0) ? powerOnHours : null,
                NamespaceId = (validBits & 0x01) != 0 ? nsid : null,
                FailingLba = (validBits & 0x02) != 0 ? lba : null,
            });
        }
        return (list, currentOpText, currentCompletion, true, string.Empty);
    }, reason => (new List<NvmeSelfTestResult>(), string.Empty, (byte)0, false, reason));

    /// <summary>#321: Short/Extended self-test trigger (NVMe Device Self-test admin command,
    /// opcode 0x14). Left deliberately unwired - issuing it requires IOCTL_STORAGE_PROTOCOL_COMMAND,
    /// a considerably larger structure (dynamically-computed error-info-buffer offsets, a full
    /// NVMe submission-queue-entry DWORD block) than the read-only IOCTL_STORAGE_QUERY_PROPERTY
    /// path every other method in this file uses, and this chunk had no way to verify that struct
    /// layout against real hardware. Per "degrade rather than fabricate", this reports plainly that
    /// it isn't wired up instead of shipping an unverified command encoding against a physical
    /// controller. The read-only log-page/Identify paths above are unaffected.</summary>
    public static (bool Success, string Message) TriggerSelfTest(int diskIndex, bool extended)
        => (false, "Not yet wired to hardware - triggering a self-test needs IOCTL_STORAGE_PROTOCOL_COMMAND, a considerably larger admin-command structure this chunk didn't have confidence verifying against real hardware. Log pages above (health/error/self-test history) are unaffected.");

    // --- #322: Identify Controller (CNS=1) + best-effort Get Features/APST follow-up -----------

    public static NvmeIdentifyInfo ReadIdentify(int diskIndex) => WithHandle(diskIndex, handle =>
    {
        var data = QueryProtocolSpecific(handle, NVMeDataTypeIdentify, IdentifyCnsController, 0, 4096, out _, out string reason);
        if (data is null) return new NvmeIdentifyInfo { Available = false, UnavailableReason = reason };

        string serial = DecodeAscii(data, 4, 20);
        string model = DecodeAscii(data, 24, 40);
        string firmware = DecodeAscii(data, 64, 8);
        byte mdts = data[77];
        byte npss = data[263];
        byte apsta = data[265];
        uint nn = BitConverter.ToUInt32(data, 516);
        bool apstSupported = (apsta & 0x01) != 0;

        bool featureOk = false;
        bool apstEnabled = false;
        int configuredStates = 0;

        // Best-effort only: the APST table's exact bit layout (Idle Time Prior to Transition /
        // Idle Transition Power State packing within each 8-byte entry) is the least-travelled
        // part of this file - if it's slightly off, the worst case is a wrong "configured state"
        // count, never a crash or a misleading pass/fail verdict, and ApstFeatureQuerySucceeded
        // gates whether any of this is shown at all.
        if (apstSupported)
        {
            var apstData = QueryProtocolSpecific(handle, NVMeDataTypeFeature, ApstFeatureId, 0, 256, out uint fixedReturn, out _);
            if (apstData is not null)
            {
                featureOk = true;
                apstEnabled = (fixedReturn & 0x01) != 0;
                int stateCount = Math.Min(npss + 1, 32);
                for (int i = 0; i < stateCount; i++)
                {
                    uint low = BitConverter.ToUInt32(apstData, i * 8);
                    uint itpt = (low >> 8) & 0xFFFFFF; // bits 31:8, milliseconds
                    if (itpt != 0) configuredStates++;
                }
            }
        }

        return new NvmeIdentifyInfo
        {
            Available = true,
            ModelNumber = model,
            SerialNumber = serial,
            FirmwareRevision = firmware,
            NamespaceCount = nn,
            MdtsRaw = mdts,
            ApstSupported = apstSupported,
            PowerStateCount = npss + 1,
            ApstFeatureQuerySucceeded = featureOk,
            ApstEnabled = apstEnabled,
            ApstConfiguredStateCount = configuredStates,
        };
    }, reason => new NvmeIdentifyInfo { Available = false, UnavailableReason = reason });
}
