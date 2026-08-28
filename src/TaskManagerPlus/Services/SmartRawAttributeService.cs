using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #301/#302/#312: on-demand raw ATA SMART attribute table for one disk, beyond the driver-
/// summarised subset SystemSpecsService.ReadSmartDetails already surfaces from
/// MSFT_StorageReliabilityCounter. Primary path is root\wmi MSStorageDriver_ATAPISmartData (a WMI
/// class, not raw interop - the storage miniport driver decodes the ATA command itself and hands
/// WMI the raw 512-byte SMART READ DATA response as-is). Where that class has no instance for this
/// disk at all (common on NVMe, and on USB/1394/SD enclosures whose bridge chip doesn't implement
/// the legacy WMI storage-driver interface - #312), a second attempt sends the same ATA SMART READ
/// DATA command directly via a SCSI ATA PASS-THROUGH(16) command wrapped in
/// IOCTL_SCSI_PASS_THROUGH_DIRECT - the one piece of genuine native interop in this feature,
/// exactly the "no tool/WMI-only alternative exists" case CLAUDE.md reserves raw P/Invoke for.
/// Both paths degrade to Unavailable+a stated reason (never a guess) when they fail, the same
/// tier as SystemSpecsService.ReadFailurePredictStatus/ReadDiskWearByIndex.
/// </summary>
public static class SmartRawAttributeService
{
    // ATA-8 standard SMART attribute table layout shared by both the WMI VendorSpecific blob and
    // a raw ATA SMART READ DATA (0xD0) response: 2-byte structure revision, then 30 fixed 12-byte
    // entries: ID(1) Flags(2 LE) Current(1) Worst(1) RawBytes(6) Reserved(1).
    private const int AttributeTableOffset = 2;
    private const int AttributeEntrySize = 12;
    private const int AttributeCount = 30;

    public static SmartRawResult Read(int diskIndex, string diskModel)
    {
        var profile = SmartVendorProfiles.Match(diskModel);
        string? pnpDeviceId = GetPnpDeviceId(diskIndex);
        string busType = ResolveBusType(GetBusTypeCode(diskIndex));
        string mediaType = ResolveMediaType(GetMediaTypeCode(diskIndex));
        int? driverWear = GetDriverWearPercent(diskIndex);
        int bytesPerSector = GetBytesPerSector(diskIndex);

        byte[]? blob = TryReadViaWmi(pnpDeviceId, out string wmiFailReason);
        string source = @"root\wmi MSStorageDriver_ATAPISmartData";

        if (blob is null)
        {
            blob = TryReadViaScsiPassThrough(diskIndex, out string ptReason);
            if (blob is not null)
            {
                source = "SCSI pass-through (SAT ATA PASS-THROUGH) - the WMI class had no data for this disk";
            }
            else
            {
                string busNote = busType is "USB" or "1394" or "SD"
                    ? $"SMART is not passed through by this enclosure's {busType}-SATA bridge. "
                    : string.Empty;
                return new SmartRawResult
                {
                    Unavailable = true,
                    UnavailableReason = $"{busNote}{wmiFailReason} SCSI pass-through fallback also failed: {ptReason}",
                    VendorProfileName = profile?.Name,
                    BusType = busType,
                    MediaType = mediaType,
                    DriverWearPercent = driverWear,
                    BytesPerSector = bytesPerSector,
                };
            }
        }

        var thresholds = pnpDeviceId is null ? new Dictionary<byte, byte>() : ReadThresholds(pnpDeviceId);
        var attributes = Decode(blob, thresholds, profile);

        return new SmartRawResult
        {
            Attributes = attributes,
            VendorProfileName = profile?.Name,
            SourceDescription = source,
            BusType = busType,
            MediaType = mediaType,
            DriverWearPercent = driverWear,
            BytesPerSector = bytesPerSector,
        };
    }

    private static List<SmartRawAttribute> Decode(byte[] blob, Dictionary<byte, byte> thresholds, SmartVendorProfile? profile)
    {
        var list = new List<SmartRawAttribute>();
        for (int i = 0; i < AttributeCount; i++)
        {
            int off = AttributeTableOffset + i * AttributeEntrySize;
            if (off + AttributeEntrySize > blob.Length) break;

            byte id = blob[off];
            if (id == 0) continue; // unused slot - drives rarely populate all 30

            ushort flags = (ushort)(blob[off + 1] | (blob[off + 2] << 8));
            byte current = blob[off + 3];
            byte worst = blob[off + 4];

            var raw = new byte[6];
            Array.Copy(blob, off + 5, raw, 0, 6);
            ulong rawValue = 0;
            for (int b = 5; b >= 0; b--) rawValue = (rawValue << 8) | raw[b];

            bool hasThreshold = thresholds.TryGetValue(id, out byte thr);
            string name = (profile is not null && profile.AttributeNames.TryGetValue(id, out var vendorName))
                ? vendorName
                : SmartAttributeLookup.Resolve(id);
            var (rawDisplay, vendorNote) = SmartVendorProfiles.DecodeRaw(id, rawValue, profile);

            list.Add(new SmartRawAttribute
            {
                Id = id,
                Name = name,
                Flags = flags,
                Current = current,
                Worst = worst,
                RawBytes = raw,
                RawValue = rawValue,
                Threshold = hasThreshold ? thr : null,
                Margin = hasThreshold ? current - thr : null,
                RawDisplay = rawDisplay,
                VendorNote = vendorNote,
            });
        }

        // #302: closest-to-failure attribute first; attributes with no published threshold (no
        // Margin) sort last rather than being mixed in ahead of ones that actually have a number.
        return list.OrderBy(a => a.Margin ?? int.MaxValue).ToList();
    }

    /// <summary>Reads the 512-byte VendorSpecific SMART data blob from root\wmi
    /// MSStorageDriver_ATAPISmartData, matched to this disk by the same PNPDeviceID-prefix scheme
    /// SystemSpecsService.ReadFailurePredictStatus already uses. Never throws past this method -
    /// the class is commonly absent on NVMe and some USB/RAID stacks (#312).</summary>
    private static byte[]? TryReadViaWmi(string? pnpDeviceId, out string reason)
    {
        reason = string.Empty;
        if (pnpDeviceId is null)
        {
            reason = "Could not resolve this disk's device path.";
            return null;
        }

        try
        {
            string needle = SystemSpecsService.NormalizeForMatch(pnpDeviceId);
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_ATAPISmartData");
            foreach (ManagementObject mo in searcher.Get())
            {
                string instanceName = (mo["InstanceName"] as string ?? string.Empty).Trim();
                if (instanceName.Length == 0) continue;
                if (!SystemSpecsService.NormalizeForMatch(instanceName).StartsWith(needle, StringComparison.Ordinal))
                    continue;

                if (mo["VendorSpecific"] is byte[] bytes && bytes.Length >= AttributeTableOffset + AttributeEntrySize)
                    return bytes;
            }
            reason = "MSStorageDriver_ATAPISmartData reported no matching instance for this disk (common on NVMe and some USB/RAID stacks).";
        }
        catch (Exception ex)
        {
            reason = $@"root\wmi MSStorageDriver_ATAPISmartData unavailable ({ex.Message}).";
        }
        return null;
    }

    /// <summary>Reads MSStorageDriver_FailurePredictThresholds (#302) the same way - a parallel
    /// 30x12 table where only the ID (offset 0) and Threshold (offset 1) of each 12-byte entry
    /// matter; the rest is reserved. Returns an empty map (never throws) when unavailable, so
    /// every attribute simply renders "—" for Threshold/Margin rather than a fabricated 0.</summary>
    private static Dictionary<byte, byte> ReadThresholds(string pnpDeviceId)
    {
        var result = new Dictionary<byte, byte>();
        try
        {
            string needle = SystemSpecsService.NormalizeForMatch(pnpDeviceId);
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_FailurePredictThresholds");
            foreach (ManagementObject mo in searcher.Get())
            {
                string instanceName = (mo["InstanceName"] as string ?? string.Empty).Trim();
                if (instanceName.Length == 0) continue;
                if (!SystemSpecsService.NormalizeForMatch(instanceName).StartsWith(needle, StringComparison.Ordinal))
                    continue;

                if (mo["VendorSpecific"] is not byte[] bytes) continue;
                for (int i = 0; i < AttributeCount; i++)
                {
                    int off = AttributeTableOffset + i * AttributeEntrySize;
                    if (off + 1 >= bytes.Length) break;
                    byte id = bytes[off];
                    if (id == 0) continue;
                    result[id] = bytes[off + 1];
                }
                break; // one instance expected per disk
            }
        }
        catch
        {
            // Thresholds simply unavailable on this driver - callers show "—" per attribute.
        }
        return result;
    }

    private static string? GetPnpDeviceId(int diskIndex)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT PNPDeviceID FROM Win32_DiskDrive WHERE Index = {diskIndex}");
            foreach (ManagementObject mo in searcher.Get())
            {
                string id = (mo["PNPDeviceID"] as string ?? string.Empty).Trim();
                if (id.Length > 0) return id;
            }
        }
        catch { /* fall through to null */ }
        return null;
    }

    private static ushort? GetBusTypeCode(int diskIndex) => ReadPhysicalDiskField(diskIndex, "BusType");
    private static ushort? GetMediaTypeCode(int diskIndex) => ReadPhysicalDiskField(diskIndex, "MediaType");

    /// <summary>MSFT_PhysicalDisk.DeviceId and Win32_DiskDrive.Index are both small integers
    /// assigned in enumeration order - the same best-effort index pairing
    /// SystemSpecsService.ReadDiskWearByIndex already relies on for Wear, reused here for
    /// BusType/MediaType so #309/#312 don't need a second, differently-matched query.</summary>
    private static ushort? ReadPhysicalDiskField(int diskIndex, string field)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage", $"SELECT DeviceId, {field} FROM MSFT_PhysicalDisk");
            foreach (ManagementObject mo in searcher.Get())
            {
                if (mo["DeviceId"] is not string deviceId || !int.TryParse(deviceId, out int index)) continue;
                if (index != diskIndex) continue;
                if (mo[field] is null) return null;
                return Convert.ToUInt16(mo[field]);
            }
        }
        catch { /* namespace/class unavailable */ }
        return null;
    }

    private static int GetBytesPerSector(int diskIndex)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT BytesPerSector FROM Win32_DiskDrive WHERE Index = {diskIndex}");
            foreach (ManagementObject mo in searcher.Get())
            {
                if (mo["BytesPerSector"] is null) continue;
                int value = Convert.ToInt32(mo["BytesPerSector"]);
                if (value > 0) return value;
            }
        }
        catch { /* fall back to the near-universal default */ }
        return 512;
    }

    private static int? GetDriverWearPercent(int diskIndex)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage", $"SELECT Wear FROM MSFT_StorageReliabilityCounter WHERE DeviceId = '{diskIndex}'");
            foreach (ManagementObject mo in searcher.Get())
            {
                if (mo["Wear"] is null) return null;
                return Convert.ToInt32(mo["Wear"]);
            }
        }
        catch { /* namespace/class unavailable */ }
        return null;
    }

    private static string ResolveBusType(ushort? code) => code switch
    {
        null => "Unknown",
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        15 => "Virtual",
        16 => "File Backed Virtual",
        17 => "Storage Spaces",
        18 => "NVMe",
        _ => "Unknown",
    };

    private static string ResolveMediaType(ushort? code) => code switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "Unknown",
    };

    // --- #312: SCSI pass-through fallback (ATA PASS-THROUGH(16), SMART READ DATA) -------------
    // Best-effort second attempt only - wrapped so any failure (access denied, bridge chip
    // doesn't support SAT ATA pass-through at all, wrong CDB for this particular controller, ...)
    // degrades to "Unavailable" with a stated reason exactly like the WMI path above, never a hang
    // or an unhandled exception.

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint IoctlScsiPassThroughDirect = 0x4D014;
    private const byte ScsiIoctlDataIn = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ScsiPassThroughDirect
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScsiPassThroughDirectWithSense
    {
        public ScsiPassThroughDirect Sptd;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
        public byte[] Sense;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string filename, uint access, uint share, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
        IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    private static byte[] BuildAtaSmartReadDataCdb16()
    {
        // ATA PASS-THROUGH(16), PIO Data-In, SMART READ DATA (features=0xD0, LBA mid/high =
        // 0x4F/0xC2 - the fixed "signature" values the ATA spec defines for the SMART command
        // set), one 512-byte sector transferred, command=0xB0 (SMART).
        return new byte[]
        {
            0x85,       // OPERATION CODE: ATA PASS-THROUGH (16)
            0x08,       // PROTOCOL = PIO Data-In (4) << 1
            0x0E,       // T_DIR=1 (from device), BYTE_BLOCK=1, T_LENGTH=2 (sector count field)
            0x00, 0xD0, // FEATURES(15:8), FEATURES(7:0) = SMART READ DATA
            0x00, 0x01, // SECTOR_COUNT(15:8), SECTOR_COUNT(7:0) = 1
            0x00, 0x00, // LBA(31:24), LBA(7:0)
            0x00, 0x4F, // LBA(39:32), LBA(15:8) = 0x4F
            0x00, 0xC2, // LBA(47:40), LBA(23:16) = 0xC2
            0x00,       // DEVICE
            0xB0,       // COMMAND = SMART
            0x00,       // CONTROL
        };
    }

    private static byte[]? TryReadViaScsiPassThrough(int diskIndex, out string reason)
    {
        reason = string.Empty;
        string path = $@"\\.\PhysicalDrive{diskIndex}";
        SafeFileHandle? handle = null;
        GCHandle dataHandle = default;
        bool dataHandleAllocated = false;
        IntPtr requestBuffer = IntPtr.Zero;

        try
        {
            handle = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                reason = $"Could not open {path} (Win32 error {Marshal.GetLastWin32Error()}).";
                return null;
            }

            byte[] dataBuffer = new byte[512];
            dataHandle = GCHandle.Alloc(dataBuffer, GCHandleType.Pinned);
            dataHandleAllocated = true;

            var request = new ScsiPassThroughDirectWithSense
            {
                Sptd = new ScsiPassThroughDirect
                {
                    Length = (ushort)Marshal.SizeOf<ScsiPassThroughDirect>(),
                    CdbLength = 16,
                    SenseInfoLength = 24,
                    DataIn = ScsiIoctlDataIn,
                    DataTransferLength = 512,
                    TimeOutValue = 5,
                    DataBuffer = dataHandle.AddrOfPinnedObject(),
                    SenseInfoOffset = (uint)Marshal.OffsetOf<ScsiPassThroughDirectWithSense>(nameof(ScsiPassThroughDirectWithSense.Sense)),
                    Cdb = BuildAtaSmartReadDataCdb16(),
                },
                Sense = new byte[24],
            };

            int size = Marshal.SizeOf<ScsiPassThroughDirectWithSense>();
            requestBuffer = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(request, requestBuffer, false);

            bool ok = DeviceIoControl(handle, IoctlScsiPassThroughDirect, requestBuffer, (uint)size, requestBuffer, (uint)size, out _, IntPtr.Zero);
            if (!ok)
            {
                reason = $"IOCTL_SCSI_PASS_THROUGH_DIRECT failed (Win32 error {Marshal.GetLastWin32Error()}) - this bridge/controller likely doesn't support ATA pass-through.";
                return null;
            }

            return dataBuffer;
        }
        catch (Exception ex)
        {
            reason = $"SCSI pass-through attempt threw: {ex.Message}";
            return null;
        }
        finally
        {
            if (requestBuffer != IntPtr.Zero) Marshal.FreeHGlobal(requestBuffer);
            if (dataHandleAllocated) dataHandle.Free();
            handle?.Dispose();
        }
    }
}
