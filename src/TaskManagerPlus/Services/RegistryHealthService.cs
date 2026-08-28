using System.IO;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #794-795: the Windows Health tab's "Registry" card - hive/hive-log file size and health
/// (#794) and RegBack backup status plus its re-enable toggle (#795). Both read the registry
/// *files on disk* (System32\config), not the live hive contents - a deliberately cheap,
/// read-mostly check that never opens/parses an offline hive itself.
/// </summary>
public static class RegistryHealthService
{
    private static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "config");
    private static readonly string RegBackDir = Path.Combine(ConfigDir, "RegBack");
    private const string ConfigManagerKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Configuration Manager";

    // #794: quick size-threshold flags, not a verdict - see RegistryHiveFileInfo's remarks.
    // SYSTEM/SOFTWARE legitimately grow over years of driver/software churn; these are generous
    // enough that only a genuinely unusual hive trips them.
    private static readonly Dictionary<string, long> OversizedThresholdBytes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SYSTEM"] = 256L * 1024 * 1024,
        ["SOFTWARE"] = 512L * 1024 * 1024,
        ["SAM"] = 16L * 1024 * 1024,
        ["SECURITY"] = 16L * 1024 * 1024,
        ["DEFAULT"] = 64L * 1024 * 1024,
        ["NTUSER.DAT"] = 256L * 1024 * 1024,
        ["UsrClass.dat"] = 128L * 1024 * 1024,
    };

    #region #794 - Hive size and health

    public static RegistryHealthSnapshot ReadHiveHealth()
    {
        var systemHives = new List<string> { "SYSTEM", "SOFTWARE", "SAM", "SECURITY", "DEFAULT" }
            .Select(name => ReadHiveFile(name, Path.Combine(ConfigDir, name)))
            .ToList();

        var userHives = new List<RegistryHiveFileInfo>();
        foreach (var (profileName, profilePath) in EnumerateProfiles())
        {
            userHives.Add(ReadHiveFile($"NTUSER.DAT ({profileName})", Path.Combine(profilePath, "NTUSER.DAT")));
            string usrClassPath = Path.Combine(profilePath, "AppData", "Local", "Microsoft", "Windows", "UsrClass.dat");
            userHives.Add(ReadHiveFile($"UsrClass.dat ({profileName})", usrClassPath));
        }

        return new RegistryHealthSnapshot { SystemHives = systemHives, UserHives = userHives };
    }

    private static RegistryHiveFileInfo ReadHiveFile(string displayName, string path)
    {
        bool exists = false;
        long size = 0;
        DateTime? lastWrite = null;
        try
        {
            var info = new FileInfo(path);
            exists = info.Exists;
            if (exists) { size = info.Length; lastWrite = info.LastWriteTime; }
        }
        catch { /* access denied - degrade to "not found", never fabricated */ }

        string baseName = displayName.Split(' ')[0];
        bool oversized = exists && OversizedThresholdBytes.TryGetValue(baseName, out var threshold) && size > threshold;

        return new RegistryHiveFileInfo
        {
            Name = displayName,
            Path = path,
            Exists = exists,
            SizeBytes = size,
            LastWriteTime = lastWrite,
            IsOversized = oversized,
            TransactionLogNotes = exists ? ReadTransactionLogNotes(path) : new List<string>(),
        };
    }

    /// <summary>#794: flags stale .LOG1/.LOG2 (the registry's own write-ahead transaction logs -
    /// normally small and frequently rotated; one that's grown large or hasn't been touched in a
    /// long time while the hive itself keeps changing suggests the hive isn't flushing/checkpointing
    /// normally) and a leftover .blf/.regtrans-ms transactional-registry log set (normally cleaned
    /// up automatically; leftovers here are usually harmless but worth noting, per this app's
    /// "quick flag, not a verdict" convention).</summary>
    private static List<string> ReadTransactionLogNotes(string hivePath)
    {
        var notes = new List<string>();
        try
        {
            var hiveInfo = new FileInfo(hivePath);
            foreach (var suffix in new[] { ".LOG1", ".LOG2" })
            {
                var logInfo = new FileInfo(hivePath + suffix);
                if (!logInfo.Exists) continue;
                if (logInfo.Length > 10L * 1024 * 1024)
                    notes.Add($"{Path.GetFileName(logInfo.FullName)} is unusually large ({Formatting.FormatBytes(logInfo.Length)}) - the hive may not be checkpointing normally.");
                else if (hiveInfo.Exists && hiveInfo.LastWriteTime - logInfo.LastWriteTime > TimeSpan.FromDays(90))
                    notes.Add($"{Path.GetFileName(logInfo.FullName)} hasn't been touched in over 90 days while the hive itself has kept changing.");
            }

            string dir = Path.GetDirectoryName(hivePath) ?? string.Empty;
            string baseName = Path.GetFileName(hivePath);
            if (Directory.Exists(dir))
            {
                var blfFiles = Directory.EnumerateFiles(dir, baseName + ".blf").Concat(Directory.EnumerateFiles(dir, baseName + "*.regtrans-ms"));
                int count = blfFiles.Count();
                if (count > 0) notes.Add($"{count} leftover transactional-registry log file(s) ({baseName}.blf / .regtrans-ms) - usually harmless, cleaned up automatically over time.");
            }
        }
        catch { /* best-effort - a missing note isn't itself an error worth surfacing */ }
        return notes;
    }

    /// <summary>Enumerates real user profiles from ProfileList (skips well-known service SIDs -
    /// LocalService/NetworkService/S-1-5-18 System - whose hives aren't useful here), the same
    /// "read the documented ProfileList inventory rather than guess from C:\Users" approach
    /// ProfileDiagnosticsService already takes elsewhere in this app.</summary>
    private static List<(string Name, string Path)> EnumerateProfiles()
    {
        var result = new List<(string, string)>();
        try
        {
            using var profileListKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (profileListKey is null) return result;

            foreach (var sid in profileListKey.GetSubKeyNames())
            {
                if (sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") continue; // System/LocalService/NetworkService
                try
                {
                    using var sidKey = profileListKey.OpenSubKey(sid);
                    if (sidKey?.GetValue("ProfileImagePath") is not string path || path.Length == 0) continue;
                    if (!Directory.Exists(path)) continue;
                    result.Add((Path.GetFileName(path.TrimEnd('\\')), path));
                }
                catch { /* one malformed SID subkey shouldn't stop the rest */ }
            }
        }
        catch { /* ProfileList unreadable - degrade to no user hives listed */ }
        return result;
    }

    #endregion

    #region #795 - Registry backup status and re-enable

    public static RegistryBackupStatus ReadBackupStatus()
    {
        bool folderExists = false;
        bool populated = false;
        DateTime? newest = null;
        long totalSize = 0;
        try
        {
            folderExists = Directory.Exists(RegBackDir);
            if (folderExists)
            {
                foreach (var file in Directory.EnumerateFiles(RegBackDir))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Length == 0) continue; // RegBack's own "reserved but empty" placeholder, not a real backup
                        populated = true;
                        totalSize += info.Length;
                        if (newest is null || info.LastWriteTime > newest) newest = info.LastWriteTime;
                    }
                    catch { /* one unreadable file shouldn't abort the scan */ }
                }
            }
        }
        catch { /* access denied - degrade to "not found" */ }

        bool? periodicBackupEnabled = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ConfigManagerKeyPath);
            if (key?.GetValue("EnablePeriodicBackup") is int v) periodicBackupEnabled = v != 0;
        }
        catch { /* degrade to Unknown */ }

        return new RegistryBackupStatus
        {
            FolderPath = RegBackDir,
            FolderExists = folderExists,
            IsPopulated = populated,
            NewestFileTime = newest,
            TotalSizeBytes = totalSize,
            PeriodicBackupEnabled = periodicBackupEnabled,
        };
    }

    /// <summary>#795: sets EnablePeriodicBackup=1 - the documented registry value (KB4098428) that
    /// restores the pre-1803 behavior of Windows automatically refreshing RegBack every 10 days via
    /// a scheduled task. Only ever called after the caller has shown this in a confirmation dialog
    /// (CLAUDE.md's mutating-action convention); journals the write (#796).</summary>
    public static (bool Success, string? Error) EnablePeriodicBackup()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(ConfigManagerKeyPath, writable: true);
            if (key is null) return (false, "Couldn't open the Configuration Manager registry key (needs Administrator).");
            int? previous = key.GetValue("EnablePeriodicBackup") as int?;
            key.SetValue("EnablePeriodicBackup", 1, RegistryValueKind.DWord);

            RegistryChangeJournalService.Record("Registry backup", "Enabled periodic RegBack refresh (EnablePeriodicBackup)",
                "HKLM", ConfigManagerKeyPath, "EnablePeriodicBackup", RegistryValueKind.DWord, previous?.ToString(), "1");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    #endregion
}
