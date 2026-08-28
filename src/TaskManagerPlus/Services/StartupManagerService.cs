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

    // #742: the rest of the autorun-location sweep - RunOnce/RunOnceEx/RunServices/
    // RunServicesOnce (all four are legacy but still honored by Windows) and the two
    // policy-enforced Run locations Group Policy's "Run these programs at user logon"/"...at
    // computer startup" settings actually write to. None of these has a StartupApproved
    // equivalent - see StartupItem.SupportsToggle.
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceExPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnceEx";
    private const string RunServicesPath = @"Software\Microsoft\Windows\CurrentVersion\RunServices";
    private const string RunServicesOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunServicesOnce";
    private const string PolicyRunPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
    private const string Wow6432Prefix = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\";

    // First byte 02 = enabled, 03 = disabled. Remaining bytes are a timestamp Windows itself writes; zero-fill is fine for our own writes.
    private static readonly byte[] EnabledFlag = { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    private static readonly byte[] DisabledFlag = { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    public List<StartupItem> Sample()
    {
        var items = new List<StartupItem>();

        AddRegistryValueItems(items, Registry.CurrentUser, RunKeyPath, ApprovedRunKeyPath, StartupSource.RegistryRunHkcu);
        AddRegistryValueItems(items, Registry.LocalMachine, RunKeyPath, ApprovedRunKeyPath, StartupSource.RegistryRunHklm);
        AddRegistryValueItems(items, Registry.LocalMachine, Wow6432Prefix + "Run", ApprovedRunKeyPath, StartupSource.RegistryRunHklmWow6432);

        AddStartupFolderItems(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, StartupSource.StartupFolderUser);
        AddStartupFolderItems(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.CurrentUser, StartupSource.StartupFolderAllUsers);

        // #742: full autorun location sweep. None of these locations has a StartupApproved
        // equivalent, so approvedPath is always null here (IsEnabled always reads true - these
        // can't be "disabled" the way Explorer disables a Run-key/Startup-folder entry, only
        // removed - and the grid's toggle is disabled for them, see StartupItem.SupportsToggle).
        AddRegistryValueItems(items, Registry.CurrentUser, RunOncePath, null, StartupSource.RegistryRunOnceHkcu);
        AddRegistryValueItems(items, Registry.LocalMachine, RunOncePath, null, StartupSource.RegistryRunOnceHklm);
        AddRegistryValueItems(items, Registry.LocalMachine, Wow6432Prefix + "RunOnce", null, StartupSource.RegistryRunOnceHklmWow6432);

        AddRunOnceExItems(items, Registry.LocalMachine, RunOnceExPath, StartupSource.RegistryRunOnceExHklm);
        AddRunOnceExItems(items, Registry.LocalMachine, Wow6432Prefix + "RunOnceEx", StartupSource.RegistryRunOnceExHklmWow6432);

        AddRegistryValueItems(items, Registry.CurrentUser, RunServicesPath, null, StartupSource.RegistryRunServicesHkcu);
        AddRegistryValueItems(items, Registry.LocalMachine, RunServicesPath, null, StartupSource.RegistryRunServicesHklm);
        AddRegistryValueItems(items, Registry.LocalMachine, Wow6432Prefix + "RunServices", null, StartupSource.RegistryRunServicesHklmWow6432);

        AddRegistryValueItems(items, Registry.CurrentUser, RunServicesOncePath, null, StartupSource.RegistryRunServicesOnceHkcu);
        AddRegistryValueItems(items, Registry.LocalMachine, RunServicesOncePath, null, StartupSource.RegistryRunServicesOnceHklm);
        AddRegistryValueItems(items, Registry.LocalMachine, Wow6432Prefix + "RunServicesOnce", null, StartupSource.RegistryRunServicesOnceHklmWow6432);

        AddRegistryValueItems(items, Registry.LocalMachine, PolicyRunPath, null, StartupSource.PolicyRunHklm);
        AddRegistryValueItems(items, Registry.CurrentUser, PolicyRunPath, null, StartupSource.PolicyRunHkcu);

        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Shared enumerator for every value-based Run/RunOnce/RunServices/RunServicesOnce/
    /// policy-Run key - approvedPath is null for every #742 location that has no StartupApproved
    /// equivalent, in which case IsEnabled is always true (nothing here can be "disabled" the way
    /// Explorer disables a Run-key/Startup-folder entry).</summary>
    private static void AddRegistryValueItems(List<StartupItem> items, RegistryKey hive, string runPath, string? approvedPath, StartupSource source)
    {
        try
        {
            using var runKey = hive.OpenSubKey(runPath);
            if (runKey is null) return;

            using var approvedKey = approvedPath is not null ? hive.OpenSubKey(approvedPath) : null;

            foreach (var valueName in runKey.GetValueNames())
            {
                if (valueName.Length == 0) continue; // the key's own (Default) value, when unset - nothing to launch
                var command = runKey.GetValue(valueName) as string ?? string.Empty;
                if (command.Length == 0) continue;

                bool enabled = true;
                if (approvedKey?.GetValue(valueName) is byte[] flag && flag.Length > 0)
                    enabled = flag[0] == 0x02;

                var (size, modified) = ReadFileInfo(ExtractPath(command));
                items.Add(new StartupItem
                {
                    Name = valueName,
                    Command = command,
                    Source = source,
                    IsEnabled = enabled,
                    FileSizeBytes = size,
                    LastModifiedUtc = modified,
                });
            }
        }
        catch
        {
            // Key inaccessible - skip this hive/view.
        }
    }

    /// <summary>#742: RunOnceEx is subkey-based rather than value-based like the other Run* keys -
    /// each numbered subkey (e.g. "0001") holds its command(s) in its own value(s), not as a flat
    /// list directly under RunOnceEx itself - so it needs its own enumeration shape. No
    /// StartupApproved equivalent exists for it either.</summary>
    private static void AddRunOnceExItems(List<StartupItem> items, RegistryKey hive, string path, StartupSource source)
    {
        try
        {
            using var exKey = hive.OpenSubKey(path);
            if (exKey is null) return;

            foreach (var subKeyName in exKey.GetSubKeyNames())
            {
                try
                {
                    using var subKey = exKey.OpenSubKey(subKeyName);
                    if (subKey is null) continue;

                    foreach (var valueName in subKey.GetValueNames())
                    {
                        var command = subKey.GetValue(valueName) as string ?? string.Empty;
                        if (command.Length == 0) continue;

                        var (size, modified) = ReadFileInfo(ExtractPath(command));
                        items.Add(new StartupItem
                        {
                            Name = string.IsNullOrEmpty(valueName) ? subKeyName : $@"{subKeyName}\{valueName}",
                            Command = command,
                            Source = source,
                            IsEnabled = true,
                            FileSizeBytes = size,
                            LastModifiedUtc = modified,
                        });
                    }
                }
                catch { /* per-subkey - skip and continue */ }
            }
        }
        catch
        {
            // Key inaccessible - skip.
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

                var (size, modified) = ReadFileInfo(file);
                items.Add(new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(fileName),
                    Command = file,
                    Source = source,
                    IsEnabled = enabled,
                    FileSizeBytes = size,
                    LastModifiedUtc = modified,
                });
            }
        }
        catch
        {
            // Folder inaccessible - skip it.
        }
    }

    /// <summary>Round 8 #21: file size and last-modified time for a startup item's target
    /// executable, if it can be resolved and still exists - best-effort, degrades to null
    /// ("Unknown") for a missing file, an unresolvable command string, or an access-denied path.</summary>
    private static (long? SizeBytes, DateTime? LastModifiedUtc) ReadFileInfo(string path)
    {
        try
        {
            if (path.Length == 0 || !File.Exists(path)) return (null, null);
            var info = new FileInfo(path);
            return (info.Length, info.LastWriteTimeUtc);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Extracts a bare file path (no arguments) from a startup entry's raw Command string, which
    /// may be a bare path, a quoted path, or a path followed by arguments. Shared with
    /// StartupDelayService (which further reduces this to just the executable name) and
    /// StartupViewModel's signature-badge check (#18), so this parsing rule lives in exactly one
    /// place rather than being duplicated per caller.
    /// </summary>
    public static string ExtractPath(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0) return string.Empty;

        if (trimmed[0] == '"')
        {
            int end = trimmed.IndexOf('"', 1);
            return end > 0 ? trimmed[1..end] : trimmed.Trim('"');
        }

        int space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    /// <summary>Flips the enabled/disabled flag for a startup item. Requires admin for HKLM/all-users entries.
    /// #742: returns a clear failure (rather than silently writing the wrong key) for any of the
    /// new autorun locations that have no StartupApproved equivalent - callers should already be
    /// gating the toggle button on StartupItem.SupportsToggle, this is defense in depth.
    /// #747: scheduled-task rows never reach here at all - StartupViewModel.Toggle routes those to
    /// ScheduledTaskService.SetEnabledAsync instead.</summary>
    public static (bool Success, string? Error) SetEnabled(StartupItem item, bool enabled)
    {
        try
        {
            (RegistryKey Hive, string ApprovedPath, string ValueName)? target = item.Source switch
            {
                StartupSource.RegistryRunHkcu => (Registry.CurrentUser, ApprovedRunKeyPath, item.Name),
                StartupSource.RegistryRunHklm => (Registry.LocalMachine, ApprovedRunKeyPath, item.Name),
                StartupSource.RegistryRunHklmWow6432 => (Registry.LocalMachine, ApprovedRunKeyPath, item.Name),
                StartupSource.StartupFolderUser => (Registry.CurrentUser, ApprovedFolderKeyPath, Path.GetFileName(item.Command)),
                StartupSource.StartupFolderAllUsers => (Registry.CurrentUser, ApprovedFolderKeyPath, Path.GetFileName(item.Command)),
                _ => null,
            };

            if (target is not { } t)
                return (false, "This autorun location has no Explorer-managed approval flag, so it can't be toggled from here.");

            using var key = t.Hive.CreateSubKey(t.ApprovedPath, writable: true);
            byte[]? previousFlag = key.GetValue(t.ValueName) as byte[];
            key.SetValue(t.ValueName, enabled ? EnabledFlag : DisabledFlag, RegistryValueKind.Binary);

            // #796: journal this write - one of the four registry-writing actions this chunk
            // routes through RegistryChangeJournalService (see its own remarks for the rest).
            RegistryChangeJournalService.Record(
                source: "Startup",
                description: $"{(enabled ? "Enabled" : "Disabled")} startup item \"{item.Name}\"",
                hive: t.Hive == Registry.CurrentUser ? "HKCU" : "HKLM",
                subKeyPath: t.ApprovedPath,
                valueName: t.ValueName,
                kind: RegistryValueKind.Binary,
                oldValueText: previousFlag is null ? null : Convert.ToHexString(previousFlag),
                newValueText: Convert.ToHexString(enabled ? EnabledFlag : DisabledFlag));

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
