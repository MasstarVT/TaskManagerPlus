using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// #686/#688/#689: per-target display configuration - current refresh rate, scaling mode, DPI
/// scale factor, rotation, and HDR/advanced-color state - via the documented Win32 "Display
/// Configuration" (CCD) API: QueryDisplayConfig/DisplayConfigGetDeviceInfo (user32.dll) plus
/// Shcore's GetDpiForMonitor for the per-monitor DPI scale. Unlike most of this app's other native
/// interop (CpuTopologyService, NetworkConnectionsService's GetExtendedTcpTable, ...), which is
/// reserved for cases with no documented API at all, these ARE documented, stable Win32 APIs - the
/// same exception CLAUDE.md/this round's task notes call out for display config specifically
/// (SystemSpecsService.ReadMonitors already reads WMI's video-controller/monitor classes, not this
/// API, but there is no WMI equivalent for refresh/scaling/rotation/HDR).
///
/// Every native call is wrapped so a struct-size mismatch, a Windows build that doesn't support a
/// given DISPLAYCONFIG_DEVICE_INFO_TYPE, or simply "no displays" degrades to an empty result or a
/// null field - never a guess, and never lets a native failure propagate out of this class.
/// </summary>
public static class DisplayConfigService
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_SUCCESS = 0;

    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;

    private const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public uint targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTL { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_SOURCE_MODE
    {
        public uint width;
        public uint height;
        public uint pixelFormat;
        public POINTL position;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_2DREGION { public uint cx; public uint cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public DISPLAYCONFIG_RATIONAL hSyncFreq;
        public DISPLAYCONFIG_RATIONAL vSyncFreq;
        public DISPLAYCONFIG_2DREGION activeSize;
        public DISPLAYCONFIG_2DREGION totalSize;
        public uint videoStandard;
        public uint scanLineOrdering;
    }

    // DISPLAYCONFIG_MODE_INFO is a native C union keyed by infoType - only the two variants this
    // class actually reads (source/target) are declared; both start at the same offset (16 bytes
    // in, after infoType/id/adapterId), and the larger of the two (targetMode, 48 bytes) already
    // matches the true union's size, so the struct's total size comes out correct (64 bytes) even
    // without declaring the third (desktop-image) variant.
    [StructLayout(LayoutKind.Explicit)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetMode;
        [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements,
        [In, Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint numModeInfoArrayElements,
        [In, Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>One active display target's live configuration - see the class remarks for exactly
    /// which Win32 API each field comes from.</summary>
    public sealed class DisplayTargetConfig
    {
        /// <summary>Normalized "DISPLAY\{pnpId}\{uniqueId}" key, built from
        /// DISPLAYCONFIG_TARGET_DEVICE_NAME's monitorDevicePath - matched against
        /// WmiMonitorConnectionParams'/WmiMonitorRawEEdidV1Block's InstanceName (which shares the
        /// same "DISPLAY\{pnpId}\{uniqueId}_N" prefix) by SystemSpecsService.ReadMonitors, since
        /// there's no direct API mapping one source's identifier to the other's.</summary>
        public string PairKey { get; init; } = string.Empty;
        public string FriendlyName { get; init; } = string.Empty;
        public int RefreshHz { get; init; }
        public int SourceWidthPx { get; init; }
        public int SourceHeightPx { get; init; }
        public string ScalingModeText { get; init; } = "Unknown";
        public string RotationText { get; init; } = "Unknown";
        public bool? HdrSupported { get; init; }
        public bool? HdrEnabled { get; init; }
        public bool? WideColorEnabled { get; init; }
        public double? DpiScalePercent { get; init; }
    }

    public static List<DisplayTargetConfig> Query()
    {
        var result = new List<DisplayTargetConfig>();
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != ERROR_SUCCESS)
                return result;
            if (pathCount == 0) return result;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != ERROR_SUCCESS)
                return result;

            var dpiByGdiName = BuildDpiByGdiDeviceName();

            for (int i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                if (path.targetInfo.targetAvailable == 0) continue;

                try
                {
                    result.Add(BuildTargetConfig(path, modes, dpiByGdiName));
                }
                catch
                {
                    // One target's device-info calls failed - skip it, others may still succeed.
                }
            }
        }
        catch
        {
            // Struct-layout/version mismatch, or the CCD API isn't available at all on this
            // Windows build - degrade to "no display-config data", never a guess.
        }
        return result;
    }

    private static DisplayTargetConfig BuildTargetConfig(DISPLAYCONFIG_PATH_INFO path, DISPLAYCONFIG_MODE_INFO[] modes, Dictionary<string, double> dpiByGdiName)
    {
        int refreshHz = path.targetInfo.refreshRate.Denominator > 0
            ? (int)Math.Round((double)path.targetInfo.refreshRate.Numerator / path.targetInfo.refreshRate.Denominator)
            : 0;

        int sourceWidth = 0, sourceHeight = 0;
        if (path.sourceInfo.modeInfoIdx < modes.Length)
        {
            var m = modes[path.sourceInfo.modeInfoIdx];
            if (m.infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
            {
                sourceWidth = (int)m.sourceMode.width;
                sourceHeight = (int)m.sourceMode.height;
            }
        }

        string friendlyName = string.Empty;
        string pairKey = string.Empty;
        var targetNamePacket = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id,
            },
        };
        if (DisplayConfigGetDeviceInfo(ref targetNamePacket) == ERROR_SUCCESS)
        {
            friendlyName = targetNamePacket.monitorFriendlyDeviceName ?? string.Empty;
            pairKey = BuildPairKey(targetNamePacket.monitorDevicePath);
        }

        string gdiDeviceName = string.Empty;
        var sourceNamePacket = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id,
            },
        };
        if (DisplayConfigGetDeviceInfo(ref sourceNamePacket) == ERROR_SUCCESS)
            gdiDeviceName = sourceNamePacket.viewGdiDeviceName ?? string.Empty;

        double? dpiPercent = gdiDeviceName.Length > 0 && dpiByGdiName.TryGetValue(gdiDeviceName, out var dpi) ? dpi : null;

        bool? hdrSupported = null, hdrEnabled = null, wideColor = null;
        var colorPacket = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id,
            },
        };
        if (DisplayConfigGetDeviceInfo(ref colorPacket) == ERROR_SUCCESS)
        {
            hdrSupported = (colorPacket.value & 0x1) != 0;
            hdrEnabled = (colorPacket.value & 0x2) != 0;
            wideColor = (colorPacket.value & 0x4) != 0;
        }

        return new DisplayTargetConfig
        {
            PairKey = pairKey,
            FriendlyName = friendlyName,
            RefreshHz = refreshHz,
            SourceWidthPx = sourceWidth,
            SourceHeightPx = sourceHeight,
            ScalingModeText = ScalingText(path.targetInfo.scaling),
            RotationText = RotationText(path.targetInfo.rotation),
            HdrSupported = hdrSupported,
            HdrEnabled = hdrEnabled,
            WideColorEnabled = wideColor,
            DpiScalePercent = dpiPercent,
        };
    }

    /// <summary>monitorDevicePath looks like "\\?\DISPLAY#GSM77F2#5&amp;148e1db3&amp;0&amp;UID4352#{guid}" -
    /// WMI's InstanceName for the same monitor looks like "DISPLAY\GSM77F2\5&amp;148e1db3&amp;0&amp;UID4352_0"
    /// (backslashes, plus a trailing "_N"). Builds the common "DISPLAY\{pnpId}\{uniqueId}" prefix
    /// so callers can match with String.StartsWith rather than needing an exact match.</summary>
    private static string BuildPairKey(string? monitorDevicePath)
    {
        if (string.IsNullOrEmpty(monitorDevicePath)) return string.Empty;
        var parts = monitorDevicePath.Split('#');
        // parts[0] = "\\?\DISPLAY", parts[1] = pnpId, parts[2] = uniqueId, parts[3] = "{guid}"
        if (parts.Length < 3) return string.Empty;
        return $@"DISPLAY\{parts[1]}\{parts[2]}";
    }

    private static string ScalingText(uint v) => v switch
    {
        1 => "Identity (no scaling)",
        2 => "Centered",
        3 => "Stretched (full screen)",
        4 => "Aspect-ratio-centered / maximized",
        5 => "Custom",
        128 => "Preferred (driver default)",
        _ => "Unknown",
    };

    private static string RotationText(uint v) => v switch
    {
        1 => "0°",
        2 => "90°",
        3 => "180°",
        4 => "270°",
        _ => "Unknown",
    };

    /// <summary>#689: per-monitor DPI scale factor (100% = 96 DPI) via Shcore's GetDpiForMonitor,
    /// keyed by the GDI device name (e.g. "\\.\DISPLAY1") EnumDisplayMonitors/GetMonitorInfo
    /// reports for each HMONITOR - the same key DISPLAYCONFIG_SOURCE_DEVICE_NAME reports for each
    /// path's source, letting the two be joined in BuildTargetConfig above.</summary>
    private static Dictionary<string, double> BuildDpiByGdiDeviceName()
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
            {
                try
                {
                    var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
                    if (GetMonitorInfo(hMonitor, ref mi) && !string.IsNullOrEmpty(mi.szDevice) &&
                        GetDpiForMonitor(hMonitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _) == ERROR_SUCCESS)
                    {
                        result[mi.szDevice] = Math.Round(dpiX / 96.0 * 100.0);
                    }
                }
                catch
                {
                    // Best-effort - one monitor's DPI lookup failing shouldn't stop the enumeration.
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // EnumDisplayMonitors/GetDpiForMonitor unavailable - degrade to "no DPI data".
        }
        return result;
    }
}
