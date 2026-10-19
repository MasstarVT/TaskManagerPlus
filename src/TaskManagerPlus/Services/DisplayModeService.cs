using System.Runtime.InteropServices;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #253/#255: display-mode/refresh-rate audit (EnumDisplayDevices + EnumDisplaySettingsEx,
/// user32.dll - no tool/WMI equivalent exposes per-mode refresh-rate lists, so raw P/Invoke is the
/// documented exception here, same tier as DwmCompositionService) plus the Game DVR/fullscreen-
/// optimisation registry audit (#255, plain registry reads). Both are fast enough to load once at
/// start-up plus a manual refresh, matching the existing #217-224 device-topology pattern - neither
/// needs its own per-tick timer.
/// </summary>
public static class DisplayModeService
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsExW")]
    private static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

    /// <summary>#253: current vs. maximum-supported refresh rate/colour depth per attached monitor,
    /// plus whether multiple monitors disagree on refresh rate - see DisplayModeAudit.</summary>
    public static DisplayModeAudit ReadAudit()
    {
        var rows = new List<DisplayModeRow>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

                // dd.DeviceString here is the adapter (GPU) name; the monitor attached to this
                // output is the first child device of dd.DeviceName.
                var row = ReadOneMonitor(dd.DeviceName, ReadAttachedMonitorName(dd.DeviceName));
                if (row is not null) rows.Add(row);
            }
        }
        catch
        {
            // best-effort - fall through with whatever rows were already collected
        }

        return new DisplayModeAudit
        {
            Monitors = rows,
            StatusText = rows.Count == 0
                ? "Couldn't enumerate any attached display's mode list on this system."
                : $"{rows.Count} attached monitor(s) read.",
        };
    }

    /// <summary>"Generic PnP Monitor (DISPLAY1)"-style label from EnumDisplayDevices(outputName, 0);
    /// the GDI output name keeps rows distinct when every monitor reports the same generic
    /// string. Empty when no monitor device is reported (the caller then falls back to the
    /// output name).</summary>
    private static string ReadAttachedMonitorName(string outputDeviceName)
    {
        try
        {
            var mon = new DISPLAY_DEVICE();
            mon.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            if (!EnumDisplayDevices(outputDeviceName, 0, ref mon, 0) || string.IsNullOrWhiteSpace(mon.DeviceString))
                return string.Empty;
            string shortOutput = outputDeviceName.StartsWith(@"\\.\", StringComparison.Ordinal) ? outputDeviceName[4..] : outputDeviceName;
            return $"{mon.DeviceString} ({shortOutput})";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static DisplayModeRow? ReadOneMonitor(string deviceName, string deviceString)
    {
        var current = new DEVMODE();
        current.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        if (!EnumDisplaySettingsEx(deviceName, ENUM_CURRENT_SETTINGS, ref current, 0)) return null;

        // Max refresh rate supported *at the current resolution* - a different (lower) resolution
        // legitimately supporting a higher refresh rate isn't "what this monitor could be doing
        // right now" the way this item means it.
        int maxHz = current.dmDisplayFrequency;
        for (int mode = 0; ; mode++)
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
            if (!EnumDisplaySettingsEx(deviceName, mode, ref dm, 0)) break;
            if (dm.dmPelsWidth == current.dmPelsWidth && dm.dmPelsHeight == current.dmPelsHeight && dm.dmDisplayFrequency > maxHz)
                maxHz = dm.dmDisplayFrequency;
            if (mode > 500) break; // sanity cap - a driver should never report this many distinct modes
        }

        return new DisplayModeRow
        {
            MonitorName = string.IsNullOrWhiteSpace(deviceString) ? deviceName : deviceString,
            CurrentWidth = current.dmPelsWidth,
            CurrentHeight = current.dmPelsHeight,
            CurrentRefreshHz = current.dmDisplayFrequency,
            MaxRefreshHz = maxHz,
            CurrentColorDepthBits = current.dmBitsPerPel,
        };
    }

    /// <summary>#255: Game DVR / fullscreen-optimisation audit - GameConfigStore, the
    /// machine-wide policy override, GameBar's auto-mode toggle, and any app under AppCompatFlags
    /// \Layers carrying the DISABLEDXMAXIMIZEDWINDOWEDMODE compatibility token (forces the classic
    /// "fullscreen optimizations off" behavior for that specific app, regardless of the global
    /// GameDVR setting).</summary>
    public static GameDvrAuditInfo ReadGameDvrAudit()
    {
        bool? dvrEnabled = ReadDwordAsBool(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled");
        bool? policyAllows = ReadDwordAsBool(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR");
        bool? policyDisabled = policyAllows is null ? null : !policyAllows.Value;
        bool? autoGameMode = ReadDwordAsBool(Registry.CurrentUser, @"SOFTWARE\Microsoft\GameBar", "AutoGameModeEnabled");

        var forced = new List<string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
            if (key is not null)
            {
                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is string val &&
                        val.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparison.OrdinalIgnoreCase))
                        forced.Add(valueName);
                }
            }
        }
        catch
        {
            // best-effort - degrade to an empty list, never a guess
        }

        string status = dvrEnabled switch
        {
            true => "Game DVR (background recording) is enabled.",
            false => "Game DVR (background recording) is disabled.",
            null => "Game DVR setting not found in the registry - using Windows' default.",
        };
        if (policyAllows is not null)
            status += policyAllows == false ? " A machine policy forces Game DVR off regardless of the per-user setting." : " A machine policy explicitly allows Game DVR.";
        if (forced.Count > 0)
            status += $" {forced.Count} app(s) have fullscreen optimizations force-disabled via a compatibility layer.";

        return new GameDvrAuditInfo
        {
            GameDvrEnabled = dvrEnabled,
            GameDvrPolicyDisabled = policyDisabled,
            GameBarAutoModeEnabled = autoGameMode,
            FullscreenOptForcedOffApps = forced,
            StatusText = status,
        };
    }

    private static bool? ReadDwordAsBool(RegistryKey root, string path, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(valueName) switch
            {
                int i => i != 0,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }
}
