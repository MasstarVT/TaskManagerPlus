using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #241: reads the handful of HKCU\Control Panel\Desktop / HKLM\...\Control hang/kill-timeout
/// values that directly change how long a freeze *feels* - exactly the values "speed up Windows"
/// tweak guides love to mangle. Reuses the existing PlatformLatencySettingRow shape so these rows
/// append naturally onto ResponsivenessViewModel.PlatformLatencySettings alongside the #220/#227/
/// #232 rows already there, rather than needing a second card/collection. AutoEndTasks and
/// ForegroundLockTimeout in particular are routinely absent entirely on a default install - that
/// means "using the Windows default", not an error, and is surfaced as such rather than "Unknown".
/// </summary>
public static class HangTimeoutRegistryService
{
    private sealed record TimeoutSetting(string DisplayName, bool IsLocalMachine, string Path, string ValueName, string DefaultText, string Unit);

    private static readonly TimeoutSetting[] Settings =
    {
        new("Hung app timeout", false, @"Control Panel\Desktop", "HungAppTimeout", "5000", "ms"),
        new("Wait to kill app timeout", false, @"Control Panel\Desktop", "WaitToKillAppTimeout", "20000", "ms"),
        new("Auto end tasks", false, @"Control Panel\Desktop", "AutoEndTasks", "0", ""),
        new("Menu show delay", false, @"Control Panel\Desktop", "MenuShowDelay", "400", "ms"),
        new("Foreground lock timeout", false, @"Control Panel\Desktop", "ForegroundLockTimeout", "0", "ms"),
        new("Wait to kill service timeout", true, @"SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", "5000", "ms"),
    };

    public static List<PlatformLatencySettingRow> ReadAudit() => Settings.Select(ReadOne).ToList();

    private static PlatformLatencySettingRow ReadOne(TimeoutSetting s)
    {
        try
        {
            // Registry.CurrentUser/Registry.LocalMachine are cached, shared root handles - only the
            // subkey opened from them gets disposed, matching ShellExtensionService's own pattern.
            RegistryKey baseKey = s.IsLocalMachine ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = baseKey.OpenSubKey(s.Path);
            object? raw = key?.GetValue(s.ValueName);

            if (raw is null)
            {
                return new PlatformLatencySettingRow
                {
                    SettingName = s.DisplayName,
                    ValueText = $"Not set — Windows default ({s.DefaultText}{(string.IsNullOrEmpty(s.Unit) ? "" : " " + s.Unit)})",
                    Note = "Absent is normal for this value; Windows applies its built-in default when it's missing.",
                };
            }

            string valueText = raw.ToString()?.Trim() ?? s.DefaultText;
            bool matchesDefault = string.Equals(valueText, s.DefaultText, StringComparison.OrdinalIgnoreCase);
            string unitSuffix = string.IsNullOrEmpty(s.Unit) ? string.Empty : $" {s.Unit}";

            return new PlatformLatencySettingRow
            {
                SettingName = s.DisplayName,
                ValueText = $"{valueText}{unitSuffix}",
                Note = matchesDefault
                    ? null
                    : $"Windows default is {s.DefaultText}{unitSuffix} — this has been changed, often by a \"speed up Windows\" tweak guide.",
            };
        }
        catch
        {
            return new PlatformLatencySettingRow { SettingName = s.DisplayName, ValueText = "Unknown", Note = "Registry read failed." };
        }
    }
}
