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
        // The five system hives are always loaded on a running system.
        var systemHives = new List<string> { "SYSTEM", "SOFTWARE", "SAM", "SECURITY", "DEFAULT" }
            .Select(name => ReadHiveFile(name, Path.Combine(ConfigDir, name), isLoaded: true))
            .ToList();

        var userHives = new List<RegistryHiveFileInfo>();
        foreach (var (profileName, profilePath, sid) in EnumerateProfiles())
        {
            bool loaded = IsUserHiveLoaded(sid);
            userHives.Add(ReadHiveFile($"NTUSER.DAT ({profileName})", Path.Combine(profilePath, "NTUSER.DAT"), loaded));
            string usrClassPath = Path.Combine(profilePath, "AppData", "Local", "Microsoft", "Windows", "UsrClass.dat");
            userHives.Add(ReadHiveFile($"UsrClass.dat ({profileName})", usrClassPath, loaded));
        }

        return new RegistryHealthSnapshot { SystemHives = systemHives, UserHives = userHives };
    }

    /// <summary>A logged-on (or service-loaded) profile's hive shows up under HKEY_USERS\{SID};
    /// when the check itself fails it is treated as loaded, which only suppresses a note.</summary>
    private static bool IsUserHiveLoaded(string sid)
    {
        try
        {
            using var key = Registry.Users.OpenSubKey(sid);
            return key is not null;
        }
        catch
        {
            return true;
        }
    }

    private static RegistryHiveFileInfo ReadHiveFile(string displayName, string path, bool isLoaded)
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
            TransactionLogNotes = exists ? ReadTransactionLogNotes(path, isLoaded) : new List<string>(),
        };
    }

    /// <summary>#794: flags an unusually large .LOG1/.LOG2 (the registry's own write-ahead
    /// transaction logs) and, for a hive that isn't loaded, more than one KTM log set beside it.
    /// No age check: Windows doesn't update a log's LastWriteTime while the hive is open, so an
    /// old timestamp is normal. Every hive carries one "{name}{guid}.TM.blf" +
    /// "TMContainer*.regtrans-ms" resource-manager set whether loaded or not, so only extra sets
    /// on an unloaded hive are noted ("quick flag, not a verdict").</summary>
    private static List<string> ReadTransactionLogNotes(string hivePath, bool isLoaded)
    {
        var notes = new List<string>();
        try
        {
            foreach (var suffix in new[] { ".LOG1", ".LOG2" })
            {
                var logInfo = new FileInfo(hivePath + suffix);
                if (!logInfo.Exists) continue;
                if (logInfo.Length > 10L * 1024 * 1024)
                    notes.Add($"{Path.GetFileName(logInfo.FullName)} is unusually large ({Formatting.FormatBytes(logInfo.Length)}) - the hive may not be checkpointing normally.");
            }

            string dir = Path.GetDirectoryName(hivePath) ?? string.Empty;
            string baseName = Path.GetFileName(hivePath);
            if (!isLoaded && Directory.Exists(dir))
            {
                int sets = Directory.EnumerateFiles(dir, baseName + "{*}.TM.blf")
                    .Count(f => f.EndsWith(".TM.blf", StringComparison.OrdinalIgnoreCase));
                if (sets > 1) notes.Add($"{sets} transactional-registry log sets ({baseName}{{guid}}.TM.blf / .regtrans-ms) beside this unloaded hive - older sets are usually harmless leftovers.");
            }
        }
        catch { /* best-effort - a missing note isn't itself an error worth surfacing */ }
        return notes;
    }

    /// <summary>Enumerates real user profiles from ProfileList (skips well-known service SIDs -
    /// LocalService/NetworkService/S-1-5-18 System - whose hives aren't useful here), the same
    /// "read the documented ProfileList inventory rather than guess from C:\Users" approach
    /// ProfileDiagnosticsService already takes elsewhere in this app.</summary>
    private static List<(string Name, string Path, string Sid)> EnumerateProfiles()
    {
        var result = new List<(string, string, string)>();
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
                    result.Add((Path.GetFileName(path.TrimEnd('\\')), path, sid));
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
