using System.IO;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 14, item 26: %SystemRoot%\Minidump folder housekeeping - total size, oldest/newest
/// file, delete-older-than-N-days, and the HKLM\SYSTEM\CurrentControlSet\Control\CrashControl\
/// MinidumpsCount registry value that actually controls how many small dumps Windows keeps
/// before recycling old ones.
/// </summary>
public static class MinidumpHousekeepingService
{
    private const string CrashControlKeyPath = @"SYSTEM\CurrentControlSet\Control\CrashControl";

    public static MinidumpHousekeepingInfo ReadHousekeeping()
    {
        int count = 0;
        long totalSize = 0;
        DateTime? oldest = null, newest = null;

        try
        {
            var dir = MinidumpParserService.MinidumpFolder;
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.dmp"))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        count++;
                        totalSize += info.Length;
                        if (oldest is null || info.LastWriteTime < oldest) oldest = info.LastWriteTime;
                        if (newest is null || info.LastWriteTime > newest) newest = info.LastWriteTime;
                    }
                    catch { /* one unreadable file shouldn't stop the tally */ }
                }
            }
        }
        catch { /* folder missing/access denied - zeros/nulls, same as an empty folder */ }

        return new MinidumpHousekeepingInfo
        {
            FileCount = count,
            TotalSizeBytes = totalSize,
            OldestFile = oldest,
            NewestFile = newest,
            MinidumpsCountRegistryValue = ReadMinidumpsCount(),
        };
    }

    private static int? ReadMinidumpsCount()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CrashControlKeyPath);
            var value = key?.GetValue("MinidumpsCount");
            return value is null ? null : Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Item 26: read/write - writing needs the elevated process this app already
    /// always runs as (CLAUDE.md's Elevation note), the same as every other registry write in
    /// this app (StartupManagerService's StartupApproved flag flips).</summary>
    public static bool WriteMinidumpsCount(int value)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CrashControlKeyPath, writable: true);
            if (key is null) return false;
            key.SetValue("MinidumpsCount", value, RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Deletes every *.dmp under the Minidump folder whose LastWriteTime is older than
    /// olderThanDays - returns the count actually deleted (best-effort per-file; one locked/
    /// denied file doesn't stop the rest).</summary>
    public static int DeleteOlderThan(int olderThanDays)
    {
        int deleted = 0;
        try
        {
            var dir = MinidumpParserService.MinidumpFolder;
            if (!Directory.Exists(dir)) return 0;
            var cutoff = DateTime.Now.AddDays(-olderThanDays);
            foreach (var file in Directory.GetFiles(dir, "*.dmp"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch { /* locked/denied - skip this one file */ }
            }
        }
        catch { /* folder missing/access denied */ }
        return deleted;
    }
}
