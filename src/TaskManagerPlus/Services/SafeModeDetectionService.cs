using System.Runtime.InteropServices;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #726: live safe-mode detection. GetSystemMetrics(SM_CLEANBOOT) is the documented Win32 API for
/// this (0 = normal boot, 1 = Safe Mode, 2 = Safe Mode with Networking) - the same "known Windows
/// API over raw interop" convention this app already follows elsewhere, since there's no shelled-
/// out tool that reports this more simply. Corroborated (never overridden) by
/// HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Option\OptionValue, a documented value Windows
/// itself writes for the current boot. Called once at MainViewModel construction - safe mode
/// can't change without a reboot, so there's nothing to poll (see CLAUDE.md's on-demand-vs-polled
/// convention).
/// </summary>
public static class SafeModeDetectionService
{
    private const int SM_CLEANBOOT = 67;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public static SafeModeInfo Detect()
    {
        int metric;
        try { metric = GetSystemMetrics(SM_CLEANBOOT); }
        catch { metric = 0; }

        var level = metric switch
        {
            1 => SafeModeLevel.Minimal,
            2 => SafeModeLevel.Network,
            _ => SafeModeLevel.Normal,
        };

        return new SafeModeInfo { Level = level, RegistryOptionValue = ReadSafeBootOption() };
    }

    private static string? ReadSafeBootOption()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SafeBoot\Option");
            return key?.GetValue("OptionValue")?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
