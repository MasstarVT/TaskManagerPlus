using System.IO;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Enumerates and toggles apps that launch at sign-in: the registry Run keys
/// and the Startup folders. Enabling/disabling mirrors what Windows itself
/// does (and what the real Task Manager's Startup tab does): it does NOT
/// delete the Run value or move the shortcut, it flips a binary flag under
/// ...\Explorer\StartupApproved so Explorer skips launching it.
/// </summary>
public sealed class StartupManagerService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedFolderKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    // First byte 02 = enabled, 03 = disabled. Remaining bytes are a timestamp Windows itself writes; zero-fill is fine for our own writes.
    private static readonly byte[] EnabledFlag = { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] DisabledFlag = { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    public List<StartupItem> Sample()
    {
        var items = new List<StartupItem>();

        AddRegistryRunItems(items, Registry.CurrentUser, RunKeyPath, ApprovedRunKeyPath, StartupSource.RegistryRunHkcu);
        AddRegistryRunItems(items, Registry.LocalMachine, RunKeyPath, ApprovedRunKeyPath, StartupSource.RegistryRunHklm);
        AddRegistryRunItems(items, Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", ApprovedRunKeyPath, StartupSource.RegistryRunHklmWow6432);

        AddStartupFolderItems(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, StartupSource.StartupFolderUser);
        AddStartupFolderItems(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.CurrentUser, StartupSource.StartupFolderAllUsers);

        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddRegistryRunItems(List<StartupItem> items, RegistryKey hive, string runPath, string approvedPath, StartupSource source)
    {
        try
        {
            using var runKey = hive.OpenSubKey(runPath);
            if (runKey is null) return;

            using var approvedKey = hive.OpenSubKey(approvedPath);

            foreach (var valueName in runKey.GetValueNames())
            {
                var command = runKey.GetValue(valueName) as string ?? string.Empty;
                bool enabled = true;
                if (approvedKey?.GetValue(valueName) is byte[] flag && flag.Length > 0)
                    enabled = flag[0] == 0x02;

                items.Add(new StartupItem
                {
                    Name = valueName,
                    Command = command,
                    Source = source,
                    IsEnabled = enabled,
                });
            }
        }
        catch
        {
            // Key inaccessible - skip this hive/view.
        }
    }

    private static void AddStartupFolderItems(List<StartupItem> items, string folderPath, RegistryKey hive, StartupSource source)
    {
        try
        {
            if (!Directory.Exists(folderPath)) return;

            using var approvedKey = hive.OpenSubKey(ApprovedFolderKeyPath);

            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                bool enabled = true;
                if (approvedKey?.GetValue(fileName) is byte[] flag && flag.Length > 0)
                    enabled = flag[0] == 0x02;

                items.Add(new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(fileName),
                    Command = file,
                    Source = source,
                    IsEnabled = enabled,
                });
            }
        }
        catch
        {
            // Folder inaccessible - skip it.
        }
    }

    /// <summary>Flips the enabled/disabled flag for a startup item. Requires admin for HKLM/all-users entries.</summary>
    public static (bool Success, string? Error) SetEnabled(StartupItem item, bool enabled)
    {
        try
        {
            var (hive, approvedPath, valueName) = item.Source switch
            {
                StartupSource.RegistryRunHkcu => (Registry.CurrentUser, ApprovedRunKeyPath, item.Name),
                StartupSource.RegistryRunHklm => (Registry.LocalMachine, ApprovedRunKeyPath, item.Name),
                StartupSource.RegistryRunHklmWow6432 => (Registry.LocalMachine, ApprovedRunKeyPath, item.Name),
                StartupSource.StartupFolderUser => (Registry.CurrentUser, ApprovedFolderKeyPath, Path.GetFileName(item.Command)),
                StartupSource.StartupFolderAllUsers => (Registry.CurrentUser, ApprovedFolderKeyPath, Path.GetFileName(item.Command)),
                _ => (Registry.CurrentUser, ApprovedRunKeyPath, item.Name),
            };

            using var key = hive.CreateSubKey(approvedPath, writable: true);
            key.SetValue(valueName, enabled ? EnabledFlag : DisabledFlag, RegistryValueKind.Binary);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
