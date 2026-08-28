using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #711: Prefetcher/ReadyBoot configuration audit - reads
/// HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters
/// (EnablePrefetcher/EnableSuperfetch) plus the SysMain service state, and flags the very common
/// "a debloat guide told me to disable this" case: boot prefetching turned off on a spinning disk,
/// where Superfetch/Prefetch genuinely helps (as opposed to a modern SSD/NVMe boot drive, where
/// Microsoft's own guidance is that disabling it is often fine or even recommended - see
/// PrefetchAuditResult.LooksLikeMistake). Reuses DiskFragmentationService.GetMediaType for the
/// system drive's HDD/SSD classification rather than a second WMI associator chain. Degrades to
/// Unknown when the key/value/service isn't readable - never a guessed value.
/// </summary>
public static class PrefetchAuditService
{
    private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";
    private const string SysMainServiceName = "SysMain";

    public static PrefetchAuditResult Read()
    {
        int? enablePrefetcher = null, enableSuperfetch = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            if (key is not null)
            {
                if (key.GetValue("EnablePrefetcher") is int p) enablePrefetcher = p;
                if (key.GetValue("EnableSuperfetch") is int s) enableSuperfetch = s;
            }
        }
        catch
        {
            // Access denied/missing key - stays Unknown (null), not a guessed default.
        }

        string sysMainStatus = "Unknown";
        try
        {
            using var sc = new ServiceController(SysMainServiceName);
            sysMainStatus = sc.Status.ToString();
        }
        catch
        {
            // Service not present or access denied - stays Unknown.
        }

        string systemDriveLetter = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\', ':') ?? "C";
        string mediaType = DiskFragmentationService.GetMediaType(systemDriveLetter);

        return new PrefetchAuditResult
        {
            EnablePrefetcher = enablePrefetcher,
            EnableSuperfetch = enableSuperfetch,
            SysMainStatus = sysMainStatus,
            SystemDriveMediaType = mediaType,
        };
    }

    /// <summary>One-click restore to Windows' own default: EnablePrefetcher/EnableSuperfetch back
    /// to 3 (both app-launch and boot prefetching), and the SysMain service's Start type back to
    /// Automatic (2) - written directly to its registry Start value, the same "flip the registry
    /// flag Explorer/Windows itself checks" tradeoff StartupManagerService's enable/disable already
    /// takes for StartupApproved, since ServiceController has no built-in "set start mode" API.
    /// Each half is attempted independently so a failure on one doesn't block the other.</summary>
    public static (bool Success, string? Error) RestoreDefaults()
    {
        var errors = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key is null) errors.Add("PrefetchParameters registry key not found or access denied.");
            else
            {
                int? prevPrefetcher = key.GetValue("EnablePrefetcher") as int?;
                int? prevSuperfetch = key.GetValue("EnableSuperfetch") as int?;
                key.SetValue("EnablePrefetcher", 3, RegistryValueKind.DWord);
                key.SetValue("EnableSuperfetch", 3, RegistryValueKind.DWord);

                // #796: journal both writes - see RegistryChangeJournalService's remarks for the
                // rest of this chunk's (deliberately partial) registry-write coverage.
                RegistryChangeJournalService.Record("Prefetch", "Restored EnablePrefetcher to default (3)",
                    "HKLM", KeyPath, "EnablePrefetcher", RegistryValueKind.DWord, prevPrefetcher?.ToString(), "3");
                RegistryChangeJournalService.Record("Prefetch", "Restored EnableSuperfetch to default (3)",
                    "HKLM", KeyPath, "EnableSuperfetch", RegistryValueKind.DWord, prevSuperfetch?.ToString(), "3");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Prefetch registry values: {ex.Message}");
        }

        try
        {
            string svcKeyPath = $@"SYSTEM\CurrentControlSet\Services\{SysMainServiceName}";
            using var svcKey = Registry.LocalMachine.OpenSubKey(svcKeyPath, writable: true);
            if (svcKey is null) errors.Add("SysMain service registry key not found or access denied.");
            else
            {
                int? prevStart = svcKey.GetValue("Start") as int?;
                svcKey.SetValue("Start", 2, RegistryValueKind.DWord); // 2 = Automatic, the Windows default
                RegistryChangeJournalService.Record("Prefetch", "Restored SysMain service start type to Automatic (2)",
                    "HKLM", svcKeyPath, "Start", RegistryValueKind.DWord, prevStart?.ToString(), "2");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"SysMain start type: {ex.Message}");
        }

        return errors.Count == 0 ? (true, null) : (false, string.Join(" ", errors));
    }
}
