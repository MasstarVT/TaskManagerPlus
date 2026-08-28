using System.Runtime.InteropServices;
using System.Text;

namespace TaskManagerPlus.Services;

/// <summary>#536/#540: a real-time reading of the currently-associated Wi-Fi radio, from the
/// native WLAN API rather than netsh's coarse signal percentage - see WlanNativeService's remarks
/// for why this is one of the "no tool/WMI gives this" P/Invoke exceptions CLAUDE.md carves out.</summary>
public sealed record WifiRadioSnapshot(
    string Ssid,
    string Bssid,
    /// <summary>#536: real RSSI in dBm from wlan_intf_opcode_rssi. Null when the driver/adapter
    /// doesn't support the opcode (rare, but not every miniport implements every OID) - shown as
    /// Unknown, never guessed from the quality percentage below.</summary>
    int? RssiDbm,
    /// <summary>#536: paired noise floor for an SNR figure. Windows exposes no standard, driver-
    /// agnostic API for this (no WLAN_INTF_OPCODE covers it) - always null. Kept as a field (rather
    /// than removed) so the SNR display and any future driver-specific source have somewhere to
    /// plug in without another data-shape change; today it always reads "Unknown", matching
    /// CLAUDE.md's degrade-never-fabricate rule rather than inventing a number.</summary>
    int? NoiseDbm,
    /// <summary>0-100 link-quality figure from WLAN_ASSOCIATION_ATTRIBUTES - the same rough figure
    /// netsh's "Signal" percentage is itself derived from, kept alongside the real dBm reading
    /// rather than replacing it.</summary>
    uint? LinkQualityPercent,
    uint? Channel,
    /// <summary>#540: 802.11n/ac/ax/... radio type decoded from DOT11_PHY_TYPE.</summary>
    string PhyType,
    /// <summary>#540: negotiated receive rate in Mbps (WLAN_ASSOCIATION_ATTRIBUTES.ulRxRate is in
    /// units of 100 Kbps).</summary>
    double? RxRateMbps,
    double? TxRateMbps)
{
    /// <summary>2.4 GHz is channels 1-14, 6 GHz starts at 33 (5935 MHz band uses channel numbers
    /// 1/5/9/... in the 1-233 range that overlap 5 GHz's numbering space, so a clean channel-number
    /// cutover isn't possible for 6 GHz - this app doesn't currently distinguish 5 vs 6 GHz for the
    /// native snapshot's own channel number, only for the netsh-sourced scan in
    /// WifiChannelScanService, which has the "Radio type : 802.11ax" (6 GHz only ships as ax/be)
    /// hint netsh provides to disambiguate. Good enough for the band label shown next to this
    /// snapshot's own channel figure.</summary>
    public string BandLabel => Channel switch
    {
        null => "Unknown",
        <= 14 => "2.4 GHz",
        _ => "5 GHz",
    };
}

/// <summary>
/// Items #536/#537/#540: wraps wlanapi.dll (the Native Wifi API) directly - the one Wi-Fi figure
/// genuinely unavailable from any shelled-out tool or WMI class on stock Windows is the real RSSI
/// in dBm (netsh's own "Signal" line is a 0-100% figure the driver itself already coarsened), so
/// this is one of the documented "no tool/WMI gives this" P/Invoke exceptions CLAUDE.md's
/// conventions carve out, same tier as CpuTopologyService's raw interop.
///
/// wlan_intf_opcode_rssi and wlan_intf_opcode_current_connection are both cheap, synchronous, local
/// reads of already-cached driver state - they don't trigger a scan and don't disturb the radio the
/// way WifiChannelScanService's netsh-driven neighbour scan does, so unlike that scan this is safe
/// to poll continuously (see WifiSignalMonitorService).
///
/// The client handle is opened once and kept for the life of this instance (Dispose closes it)
/// rather than reopened per call, since WlanOpenHandle/WlanCloseHandle are the more expensive half
/// of a query and this is meant to be called every couple of seconds. Every public entry point is
/// wrapped to degrade to null on any failure - no WLAN AutoConfig service, no Wi-Fi adapter, a
/// disconnected interface, or an unsupported opcode all look the same to callers: "nothing to show
/// right now", never a thrown exception and never a fabricated reading.
/// </summary>
public sealed class WlanNativeService : IDisposable
{
    private const uint ClientVersion = 2; // WLAN_API_VERSION_2_0 (Vista+) - every OS this app targets supports it.

    private IntPtr _clientHandle = IntPtr.Zero;
    private bool _openFailed;
    private readonly object _lock = new();

    public WifiRadioSnapshot? GetSnapshot()
    {
        lock (_lock)
        {
            if (!EnsureOpen()) return null;
            try
            {
                var ifaceGuid = FindConnectedInterfaceGuid();
                if (ifaceGuid is null) return null;

                var conn = QueryCurrentConnection(ifaceGuid.Value);
                if (conn is null || conn.Value.isState != (uint)WlanInterfaceState.wlan_interface_state_connected)
                    return null;

                var assoc = conn.Value.wlanAssociationAttributes;
                string ssid = DecodeSsid(assoc.dot11Ssid);
                if (string.IsNullOrEmpty(ssid)) return null;

                int? rssi = QueryRssi(ifaceGuid.Value);
                uint? channel = QueryChannelNumber(ifaceGuid.Value);

                return new WifiRadioSnapshot(
                    ssid,
                    FormatBssid(assoc.dot11Bssid),
                    rssi,
                    null,
                    assoc.wlanSignalQuality,
                    channel,
                    PhyTypeName((Dot11PhyType)assoc.dot11PhyType),
                    assoc.ulRxRate == 0 ? null : assoc.ulRxRate / 10.0,
                    assoc.ulTxRate == 0 ? null : assoc.ulTxRate / 10.0);
            }
            catch
            {
                // Marshaling hiccup, a driver returning an unexpected size, etc. - degrade to
                // "nothing to show" rather than crash the polling loop that calls this every tick.
                return null;
            }
        }
    }

    /// <summary>The interface GUID (as a string) of the currently-connected Wi-Fi adapter, if any -
    /// used by WifiChannelScanService/WifiProfileService to scope their own netsh calls to the same
    /// adapter this snapshot reads from, on a machine with more than one wireless NIC.</summary>
    public string? GetConnectedInterfaceGuid()
    {
        lock (_lock)
        {
            if (!EnsureOpen()) return null;
            try
            {
                return FindConnectedInterfaceGuid()?.ToString("B");
            }
            catch
            {
                return null;
            }
        }
    }

    private bool EnsureOpen()
    {
        if (_clientHandle != IntPtr.Zero) return true;
        if (_openFailed) return false;
        try
        {
            int result = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var handle);
            if (result != 0 || handle == IntPtr.Zero)
            {
                _openFailed = true;
                return false;
            }
            _clientHandle = handle;
            return true;
        }
        catch
        {
            // wlanapi.dll missing (Server Core, WLAN AutoConfig service not installed) - the
            // whole Wi-Fi card degrades to hidden, same as "no Wi-Fi adapter".
            _openFailed = true;
            return false;
        }
    }

    private Guid? FindConnectedInterfaceGuid()
    {
        if (WlanEnumInterfaces(_clientHandle, IntPtr.Zero, out IntPtr listPtr) != 0 || listPtr == IntPtr.Zero)
            return null;
        try
        {
            uint count = (uint)Marshal.ReadInt32(listPtr, 0);
            int itemSize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
            IntPtr itemsStart = IntPtr.Add(listPtr, 8); // header is 2x DWORD (dwNumberOfItems, dwIndex)

            for (int i = 0; i < count; i++)
            {
                IntPtr itemPtr = IntPtr.Add(itemsStart, i * itemSize);
                var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(itemPtr);
                if (info.isState == WlanInterfaceState.wlan_interface_state_connected)
                    return info.InterfaceGuid;
            }
        }
        finally
        {
            WlanFreeMemory(listPtr);
        }
        return null;
    }

    private WLAN_CONNECTION_ATTRIBUTES? QueryCurrentConnection(Guid ifaceGuid)
    {
        Guid g = ifaceGuid;
        int result = WlanQueryInterface(_clientHandle, ref g, WlanIntfOpcode.wlan_intf_opcode_current_connection,
            IntPtr.Zero, out _, out IntPtr data, IntPtr.Zero);
        if (result != 0 || data == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(data);
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private int? QueryRssi(Guid ifaceGuid)
    {
        Guid g = ifaceGuid;
        int result = WlanQueryInterface(_clientHandle, ref g, WlanIntfOpcode.wlan_intf_opcode_rssi,
            IntPtr.Zero, out uint size, out IntPtr data, IntPtr.Zero);
        if (result != 0 || data == IntPtr.Zero || size < 4) return null;
        try
        {
            return Marshal.ReadInt32(data);
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private uint? QueryChannelNumber(Guid ifaceGuid)
    {
        Guid g = ifaceGuid;
        int result = WlanQueryInterface(_clientHandle, ref g, WlanIntfOpcode.wlan_intf_opcode_channel_number,
            IntPtr.Zero, out uint size, out IntPtr data, IntPtr.Zero);
        if (result != 0 || data == IntPtr.Zero || size < 4) return null;
        try
        {
            return (uint)Marshal.ReadInt32(data);
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    private static string DecodeSsid(DOT11_SSID ssid)
    {
        if (ssid.ucSSID is null) return string.Empty;
        int len = (int)Math.Min(ssid.uSSIDLength, (uint)ssid.ucSSID.Length);
        if (len <= 0) return string.Empty;
        try { return Encoding.UTF8.GetString(ssid.ucSSID, 0, len); }
        catch { return string.Empty; }
    }

    private static string FormatBssid(byte[]? bssid)
    {
        if (bssid is null || bssid.Length != 6) return string.Empty;
        return string.Join(":", bssid.Select(b => b.ToString("X2")));
    }

    /// <summary>#540: DOT11_PHY_TYPE -> the marketing name shown on the box (802.11n/ac/ax/be), the
    /// same translation every consumer Wi-Fi analyzer performs.</summary>
    private static string PhyTypeName(Dot11PhyType phy) => phy switch
    {
        Dot11PhyType.dot11_phy_type_fhss or Dot11PhyType.dot11_phy_type_dsss or Dot11PhyType.dot11_phy_type_irbaseband => "802.11 (legacy)",
        Dot11PhyType.dot11_phy_type_ofdm => "802.11a",
        Dot11PhyType.dot11_phy_type_hrdsss => "802.11b",
        Dot11PhyType.dot11_phy_type_erp => "802.11g",
        Dot11PhyType.dot11_phy_type_ht => "802.11n",
        Dot11PhyType.dot11_phy_type_vht => "802.11ac",
        Dot11PhyType.dot11_phy_type_dmg => "802.11ad",
        Dot11PhyType.dot11_phy_type_he => "802.11ax",
        Dot11PhyType.dot11_phy_type_eht => "802.11be",
        _ => "Unknown",
    };

    public void Dispose()
    {
        lock (_lock)
        {
            if (_clientHandle != IntPtr.Zero)
            {
                try { WlanCloseHandle(_clientHandle, IntPtr.Zero); } catch { /* best-effort */ }
                _clientHandle = IntPtr.Zero;
            }
        }
    }

    // ---- native declarations (wlanapi.h) -----------------------------------------------------

    private enum WlanInterfaceState : uint
    {
        wlan_interface_state_not_ready = 0,
        wlan_interface_state_connected = 1,
        wlan_interface_state_ad_hoc_network_formed = 2,
        wlan_interface_state_disconnecting = 3,
        wlan_interface_state_disconnected = 4,
        wlan_interface_state_associating = 5,
        wlan_interface_state_discovering = 6,
        wlan_interface_state_authenticating = 7,
    }

    private enum WlanIntfOpcode : uint
    {
        wlan_intf_opcode_current_connection = 7,
        wlan_intf_opcode_channel_number = 8,
        wlan_intf_opcode_rssi = 0x10000102, // wlan_intf_opcode_msm_start(0x10000100) + 2
    }

    private enum Dot11PhyType : uint
    {
        dot11_phy_type_unknown = 0,
        dot11_phy_type_fhss = 1,
        dot11_phy_type_dsss = 2,
        dot11_phy_type_irbaseband = 3,
        dot11_phy_type_ofdm = 4,
        dot11_phy_type_hrdsss = 5,
        dot11_phy_type_erp = 6,
        dot11_phy_type_ht = 7,
        dot11_phy_type_vht = 8,
        dot11_phy_type_dmg = 9,
        dot11_phy_type_he = 10,
        dot11_phy_type_eht = 11,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;
        public WlanInterfaceState isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID dot11Ssid;
        public uint dot11BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;
        public uint dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate;
        public uint ulTxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_SECURITY_ATTRIBUTES
    {
        [MarshalAs(UnmanagedType.Bool)] public bool bSecurityEnabled;
        [MarshalAs(UnmanagedType.Bool)] public bool bOneXEnabled;
        public uint dot11AuthAlgorithm;
        public uint dot11CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_ATTRIBUTES
    {
        public uint isState;
        public uint wlanConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
        public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, WlanIntfOpcode OpCode,
        IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);
}
