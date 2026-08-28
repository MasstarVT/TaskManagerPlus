using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, #379/#381: negotiated-vs-rated SATA link speed, and negotiated-vs-max NVMe PCIe link
/// speed/width. Both are the "documented low-level API, no higher-level wrapper exists" tier
/// CLAUDE.md reserves raw P/Invoke for - reusing the exact CreateFile/DeviceIoControl SCSI-pass-
/// through scaffolding SmartRawAttributeService's #312 fallback already established (a fresh, local
/// copy of the P/Invoke declarations, same as every other service file in this app that touches
/// DeviceIoControl - none of them share a common interop helper class).
///
/// #379 (SATA): reads ATA IDENTIFY DEVICE (not SMART READ DATA) via the same ATA PASS-THROUGH(16)
/// CDB shape - words 76 (SATA Capabilities: which generations this drive supports) and 77 (SATA
/// Additional Capabilities: currently negotiated generation) are standard ATA-8/ACS fields.
///
/// #381 (NVMe): PCIe link speed/width live on the NVMe controller's own PCI function devnode, not
/// the disk's SCSI-enumerated devnode Win32_DiskDrive reports - this walks up the device tree via
/// CM_Get_Parent looking for a "PCI\" instance ID, then reads DEVPKEY_PciDevice_Current/MaxLinkSpeed/
/// Width via CM_Get_DevNode_Property. Flagged lower-confidence than #379 in this app's own remarks:
/// the exact DEVPKEY GUID/PID values below are used from best-available documentation recollection
/// without a way to verify them against the Windows SDK headers in this environment - if they're
/// wrong, CM_Get_DevNode_Property simply fails and this degrades to "Unknown", never a fabricated
/// number.
/// </summary>
public static class StorageLinkService
{
    // --- Win32 plumbing (SCSI pass-through, same shape as SmartRawAttributeService) -----------

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

    private static byte[] BuildAtaIdentifyDeviceCdb16()
    {
        // ATA PASS-THROUGH(16), PIO Data-In, IDENTIFY DEVICE (command=0xEC) - no special LBA
        // "signature" bytes needed (those - 0x4F/0xC2 - are only required for the SMART subcommand
        // set SmartRawAttributeService's CDB uses), one 512-byte sector transferred.
        return new byte[]
        {
            0x85,       // OPERATION CODE: ATA PASS-THROUGH (16)
            0x08,       // PROTOCOL = PIO Data-In (4) << 1
            0x0E,       // T_DIR=1 (from device), BYTE_BLOCK=1, T_LENGTH=2 (sector count field)
            0x00, 0x00, // FEATURES
            0x00, 0x01, // SECTOR_COUNT(15:8), SECTOR_COUNT(7:0) = 1
            0x00, 0x00, // LBA(31:24), LBA(7:0)
            0x00, 0x00, // LBA(39:32), LBA(15:8)
            0x00, 0x00, // LBA(47:40), LBA(23:16)
            0x00,       // DEVICE
            0xEC,       // COMMAND = IDENTIFY DEVICE
            0x00,       // CONTROL
        };
    }

    private static byte[]? ReadAtaIdentify(int diskIndex, out string reason)
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
                    Cdb = BuildAtaIdentifyDeviceCdb16(),
                },
                Sense = new byte[24],
            };

            int size = Marshal.SizeOf<ScsiPassThroughDirectWithSense>();
            requestBuffer = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(request, requestBuffer, false);

            bool ok = DeviceIoControl(handle, IoctlScsiPassThroughDirect, requestBuffer, (uint)size, requestBuffer, (uint)size, out _, IntPtr.Zero);
            if (!ok)
            {
                reason = $"IOCTL_SCSI_PASS_THROUGH_DIRECT (IDENTIFY DEVICE) failed (Win32 error {Marshal.GetLastWin32Error()}) - this bridge/controller likely doesn't support ATA pass-through.";
                return null;
            }

            return dataBuffer;
        }
        catch (Exception ex)
        {
            reason = $"ATA IDENTIFY pass-through attempt threw: {ex.Message}";
            return null;
        }
        finally
        {
            if (requestBuffer != IntPtr.Zero) Marshal.FreeHGlobal(requestBuffer);
            if (dataHandleAllocated) dataHandle.Free();
            handle?.Dispose();
        }
    }

    /// <summary>#379: negotiated vs. max-supported SATA generation for one disk, decoded from ATA
    /// IDENTIFY DEVICE words 76/77 (the same fields smartctl/hdparm read for "SATA Version is:
    /// ... (current: ...)"). Only meaningful for SATA/ATA-bus disks - callers should gate on
    /// BusType first (see SmartRawResult.BusType from the same SMART read this piggybacks on).
    /// </summary>
    public static DiskLinkInfo ReadSataLinkInfo(int diskIndex)
    {
        var data = ReadAtaIdentify(diskIndex, out string reason);
        if (data is null)
            return new DiskLinkInfo { IsSata = true, SataAvailable = false, SataUnavailableReason = reason };

        ushort word76 = (ushort)(data[152] | (data[153] << 8));
        ushort word77 = (ushort)(data[154] | (data[155] << 8));

        int? maxGen = null;
        if ((word76 & 0x0008) != 0) maxGen = 3;
        else if ((word76 & 0x0004) != 0) maxGen = 2;
        else if ((word76 & 0x0002) != 0) maxGen = 1;

        int negotiated = (word77 >> 1) & 0x7;
        int? negotiatedGen = negotiated is >= 1 and <= 3 ? negotiated : null;

        if (maxGen is null && negotiatedGen is null)
            return new DiskLinkInfo
            {
                IsSata = true,
                SataAvailable = false,
                SataUnavailableReason = "IDENTIFY DEVICE responded, but reported no SATA capability/negotiated-speed bits (word 76/77 both zero) - this may not be a SATA device, or the bridge/controller doesn't pass these fields through.",
            };

        return new DiskLinkInfo
        {
            IsSata = true,
            SataAvailable = true,
            SataMaxSupportedGen = maxGen,
            SataNegotiatedGen = negotiatedGen,
        };
    }

    // --- #381: NVMe PCIe link speed/width, via CM_Get_DevNode_Property -------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    private const uint DevPropTypeUInt32 = 0x00000007;
    private const uint CrSuccess = 0;
    private const uint CmLocateDevNodeNormal = 0;

    // DEVPKEY_PciDevice_Current/MaxLinkSpeed/Width - fmtid shared across the DEVPKEY_PciDevice_*
    // family. See this file's remarks: used from best-available recollection, not verified against
    // the Windows SDK in this environment - a wrong pid simply makes CM_Get_DevNode_Property fail,
    // which this treats as "Unknown", never a fabricated number.
    private static readonly Guid PciDevicePropertyFmtid = new("48F5FB93-14BC-4706-9AB8-1A0D18E7B32F");
    private static DevPropKey CurrentLinkSpeedKey => new() { Fmtid = PciDevicePropertyFmtid, Pid = 26 };
    private static DevPropKey CurrentLinkWidthKey => new() { Fmtid = PciDevicePropertyFmtid, Pid = 27 };
    private static DevPropKey MaxLinkSpeedKey => new() { Fmtid = PciDevicePropertyFmtid, Pid = 28 };
    private static DevPropKey MaxLinkWidthKey => new() { Fmtid = PciDevicePropertyFmtid, Pid = 29 };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceId, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint dnDevInst, System.Text.StringBuilder buffer, uint bufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_DevNode_PropertyW(
        uint dnDevInst, ref DevPropKey propertyKey, out uint propertyType, byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    private static int? ReadUInt32Property(uint devInst, DevPropKey key)
    {
        var propKey = key;
        uint size = 4;
        var buffer = new byte[4];
        uint cr = CM_Get_DevNode_PropertyW(devInst, ref propKey, out uint type, buffer, ref size, 0);
        if (cr != CrSuccess || type != DevPropTypeUInt32 || size < 4) return null;
        return BitConverter.ToInt32(buffer, 0);
    }

    /// <summary>Walks up to 6 device-tree parents from <paramref name="devInst"/> looking for a
    /// "PCI\" instance ID - the NVMe controller's own PCI function, which is where the link-speed/
    /// width DEVPKEYs actually live (not on the disk's SCSI-enumerated devnode). Bounded so a
    /// malformed/circular device tree can't loop forever.</summary>
    private static uint? FindPciAncestor(uint devInst)
    {
        uint current = devInst;
        for (int hop = 0; hop < 6; hop++)
        {
            var idBuilder = new System.Text.StringBuilder(512);
            if (CM_Get_Device_IDW(current, idBuilder, (uint)idBuilder.Capacity, 0) == CrSuccess &&
                idBuilder.ToString().StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
                return current;

            if (CM_Get_Parent(out uint parent, current, 0) != CrSuccess) return null;
            current = parent;
        }
        return null;
    }

    /// <summary>#381: negotiated vs. max PCIe link speed/width for the NVMe controller behind one
    /// disk. Only meaningful for NVMe disks - callers should gate on BusType first, same as #379.
    /// </summary>
    public static DiskLinkInfo ReadNvmeLinkInfo(string? pnpDeviceId)
    {
        if (string.IsNullOrEmpty(pnpDeviceId))
            return new DiskLinkInfo { IsNvme = true, NvmeAvailable = false, NvmeUnavailableReason = "Could not resolve this disk's device path." };

        try
        {
            if (CM_Locate_DevNodeW(out uint devInst, pnpDeviceId, CmLocateDevNodeNormal) != CrSuccess)
                return new DiskLinkInfo { IsNvme = true, NvmeAvailable = false, NvmeUnavailableReason = "Could not locate this disk's device node (CM_Locate_DevNode failed)." };

            uint? pciDevInst = FindPciAncestor(devInst);
            if (pciDevInst is null)
                return new DiskLinkInfo { IsNvme = true, NvmeAvailable = false, NvmeUnavailableReason = "Could not find a PCI ancestor device node for this disk within 6 hops of the device tree - the driver stack may not expose the controller the way this check expects." };

            int? currentSpeed = ReadUInt32Property(pciDevInst.Value, CurrentLinkSpeedKey);
            int? currentWidth = ReadUInt32Property(pciDevInst.Value, CurrentLinkWidthKey);
            int? maxSpeed = ReadUInt32Property(pciDevInst.Value, MaxLinkSpeedKey);
            int? maxWidth = ReadUInt32Property(pciDevInst.Value, MaxLinkWidthKey);

            if (currentSpeed is null && currentWidth is null && maxSpeed is null && maxWidth is null)
                return new DiskLinkInfo
                {
                    IsNvme = true,
                    NvmeAvailable = false,
                    NvmeUnavailableReason = "This system/driver didn't report PCIe link speed/width device properties for this controller (DEVPKEY_PciDevice_Current/MaxLinkSpeed/Width) - not available on every chipset/driver combination.",
                };

            return new DiskLinkInfo
            {
                IsNvme = true,
                NvmeAvailable = true,
                NvmeCurrentLinkSpeedGen = currentSpeed,
                NvmeCurrentLinkWidth = currentWidth,
                NvmeMaxLinkSpeedGen = maxSpeed,
                NvmeMaxLinkWidth = maxWidth,
            };
        }
        catch (Exception ex)
        {
            return new DiskLinkInfo { IsNvme = true, NvmeAvailable = false, NvmeUnavailableReason = $"PCIe link property read threw: {ex.Message}" };
        }
    }

    /// <summary>PCIe link-speed generation number (1-5) to its GT/s rating, for display.</summary>
    public static string PcieGenText(int? gen) => gen switch
    {
        1 => "Gen1 (2.5 GT/s)",
        2 => "Gen2 (5.0 GT/s)",
        3 => "Gen3 (8.0 GT/s)",
        4 => "Gen4 (16.0 GT/s)",
        5 => "Gen5 (32.0 GT/s)",
        _ => "Unknown",
    };

    /// <summary>SATA generation number (1-3) to its Gb/s rating, for display.</summary>
    public static string SataGenText(int? gen) => gen switch
    {
        1 => "SATA I (1.5 Gb/s)",
        2 => "SATA II (3.0 Gb/s)",
        3 => "SATA III (6.0 Gb/s)",
        _ => "Unknown",
    };
}
