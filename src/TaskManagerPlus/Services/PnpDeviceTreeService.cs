using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #468: the Devices &amp; Drivers tab's device-tree view (its second top-level view, grouped by
/// PNPClass to match Device Manager's own default "devices by type" grouping) - Win32_PnPEntity is
/// the primary source for every currently-present device (ListPresentAsync), the same "prefer WMI
/// over raw interop" convention this app uses everywhere.
///
/// #471: Win32_PnPEntity only ever enumerates present devices - there's no WMI equivalent for
/// "ghost"/non-present devices (a device that was plugged in once and later removed, whose registry
/// entry Windows still keeps around), so ListNonPresentAsync is this app's one deliberate exception
/// to that convention: SetupDiGetClassDevs(DIGCF_ALLCLASSES) *without* DIGCF_PRESENT, wrapped
/// defensively (every native call try/catch'd, the device-info-set handle always released) per
/// CLAUDE.md's "raw P/Invoke reserved for gaps with no WMI/tool equivalent, always degrade
/// gracefully" rule. Both lists are read fresh on every load - this tab is on-demand, no timer.
/// </summary>
public static class PnpDeviceTreeService
{
    public static Task<List<PnpDeviceNode>> ListPresentAsync() => Task.Run(ListPresent);

    private static List<PnpDeviceNode> ListPresent()
    {
        var nodes = new List<PnpDeviceNode>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, PNPClass, ClassGuid, Manufacturer, Status, ConfigManagerErrorCode, HardwareID, Service FROM Win32_PnPEntity");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    string deviceId = (mo["DeviceID"] as string ?? string.Empty).Trim();
                    if (deviceId.Length == 0) continue;

                    int errorCode = 0;
                    try { errorCode = Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0); } catch { /* leave 0 */ }

                    string className = (mo["PNPClass"] as string) is { Length: > 0 } pc ? pc : "Unknown";

                    nodes.Add(new PnpDeviceNode
                    {
                        DeviceId = deviceId,
                        Name = (mo["Name"] as string) is { Length: > 0 } n ? n.Trim() : deviceId,
                        ClassGuid = (mo["ClassGuid"] as string ?? string.Empty).Trim(),
                        ClassName = className,
                        Manufacturer = (mo["Manufacturer"] as string) is { Length: > 0 } m ? m.Trim() : "Unknown",
                        Status = (mo["Status"] as string ?? "Unknown").Trim(),
                        ConfigManagerErrorCode = errorCode,
                        HardwareIds = mo["HardwareID"] as string[] ?? Array.Empty<string>(),
                        Service = (mo["Service"] as string)?.Trim() is { Length: > 0 } svc ? svc : null,
                        IsPresent = true,
                    });
                }
            }
        }
        catch
        {
            // WMI unavailable/hiccup - return whatever was gathered before the failure.
        }
        return SortedByClassThenName(nodes);
    }

    /// <summary>#471: devices SetupDiGetClassDevs(DIGCF_ALLCLASSES) finds that ListPresent's
    /// Win32_PnPEntity sweep didn't - i.e. the actual non-present/"ghost" set, not a duplicate of
    /// the present list. Takes the already-loaded present-device IDs so this doesn't need its own
    /// second WMI pass just to compute the difference.</summary>
    public static Task<List<PnpDeviceNode>> ListNonPresentAsync(IEnumerable<string> presentDeviceIds) =>
        Task.Run(() => ListNonPresent(new HashSet<string>(presentDeviceIds, StringComparer.OrdinalIgnoreCase)));

    private static List<PnpDeviceNode> ListNonPresent(HashSet<string> presentDeviceIds)
    {
        var results = new List<PnpDeviceNode>();
        IntPtr deviceInfoSet = IntPtr.Zero;
        try
        {
            deviceInfoSet = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == InvalidHandleValue) return results;

            var devInfoData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            uint index = 0;
            while (SetupDiEnumDeviceInfo(deviceInfoSet, index, ref devInfoData))
            {
                index++;
                try
                {
                    string? deviceId = GetDeviceInstanceId(deviceInfoSet, ref devInfoData);
                    if (string.IsNullOrEmpty(deviceId)) continue;
                    if (presentDeviceIds.Contains(deviceId)) continue; // already covered by ListPresent

                    string name = GetDeviceRegistryPropertyString(deviceInfoSet, ref devInfoData, SPDRP_FRIENDLYNAME)
                        ?? GetDeviceRegistryPropertyString(deviceInfoSet, ref devInfoData, SPDRP_DEVICEDESC)
                        ?? deviceId;
                    string className = GetDeviceRegistryPropertyString(deviceInfoSet, ref devInfoData, SPDRP_CLASS) ?? "Unknown";
                    string manufacturer = GetDeviceRegistryPropertyString(deviceInfoSet, ref devInfoData, SPDRP_MFG) ?? "Unknown";
                    string[] hardwareIds = GetDeviceRegistryPropertyMultiString(deviceInfoSet, ref devInfoData, SPDRP_HARDWAREID) ?? Array.Empty<string>();

                    int errorCode = 0;
                    try
                    {
                        if (CM_Get_DevNode_Status(out _, out uint problem, devInfoData.DevInst, 0) == CR_SUCCESS)
                            errorCode = (int)problem;
                    }
                    catch { /* leave 0 - "no problem reported" is the safe degrade here */ }

                    results.Add(new PnpDeviceNode
                    {
                        DeviceId = deviceId,
                        Name = name,
                        ClassGuid = devInfoData.ClassGuid.ToString("B"),
                        ClassName = className,
                        Manufacturer = manufacturer,
                        Status = "Not present",
                        ConfigManagerErrorCode = errorCode,
                        HardwareIds = hardwareIds,
                        Service = ReadServiceFromRegistry(deviceId),
                        IsPresent = false,
                    });
                }
                catch
                {
                    // One malformed/inaccessible device entry shouldn't stop the rest of the enumeration.
                }
            }
        }
        catch
        {
            // SetupDiGetClassDevs/enumeration unavailable - degrade to an empty list rather than throw.
        }
        finally
        {
            if (deviceInfoSet != IntPtr.Zero && deviceInfoSet != InvalidHandleValue)
            {
                try { SetupDiDestroyDeviceInfoList(deviceInfoSet); } catch { /* best-effort */ }
            }
        }
        return SortedByClassThenName(results);
    }

    private static string? ReadServiceFromRegistry(string deviceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{deviceId}");
            return key?.GetValue("Service") as string is { Length: > 0 } s ? s : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<PnpDeviceNode> SortedByClassThenName(List<PnpDeviceNode> nodes) =>
        nodes.OrderBy(n => n.ClassName, StringComparer.OrdinalIgnoreCase)
             .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
             .ToList();

    // --- native interop (#471 only - see class remarks) ---

    private static readonly IntPtr InvalidHandleValue = new(-1);

    private const uint DIGCF_ALLCLASSES = 0x4;
    private const uint SPDRP_DEVICEDESC = 0x0;
    private const uint SPDRP_HARDWAREID = 0x1;
    private const uint SPDRP_CLASS = 0x7;
    private const uint SPDRP_MFG = 0xB;
    private const uint SPDRP_FRIENDLYNAME = 0xC;
    private const int CR_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    // setupapi.dll only ever exports the A/W-suffixed forms of any function taking a string
    // parameter (there is no plain "SetupDiGetClassDevs"/"SetupDiGetDeviceRegistryProperty"
    // symbol) - CharSet = Unicode is required on every one of these so the P/Invoke marshaller
    // probes for the *W export and the buffer this code reads back really is UTF-16, matching the
    // Encoding.Unicode.GetString calls below. Getting this wrong wouldn't fail loudly - the ANSI
    // fallback exists too, so it would silently marshal/decode as the wrong string type.
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, [MarshalAs(UnmanagedType.LPWStr)] string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property, out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problemNumber, uint devInst, uint flags);

    private static string? GetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfoData)
    {
        var sb = new StringBuilder(512);
        return SetupDiGetDeviceInstanceId(deviceInfoSet, ref devInfoData, sb, sb.Capacity, out _) ? sb.ToString() : null;
    }

    private static string? GetDeviceRegistryPropertyString(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfoData, uint property)
    {
        var buffer = new byte[2048];
        if (!SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfoData, property, out _, buffer, (uint)buffer.Length, out uint required) || required < 2)
            return null;

        string s = Encoding.Unicode.GetString(buffer, 0, (int)required).TrimEnd('\0');
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static string[]? GetDeviceRegistryPropertyMultiString(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfoData, uint property)
    {
        var buffer = new byte[4096];
        if (!SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devInfoData, property, out _, buffer, (uint)buffer.Length, out uint required) || required < 2)
            return null;

        string raw = Encoding.Unicode.GetString(buffer, 0, (int)required);
        return raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
