using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #483: rollback-availability detection plus launching Device Manager's own driver-properties
/// sheet so the user can click "Roll Back Driver" there themselves - there's no documented pnputil/
/// CLI verb that performs a rollback directly (unlike every other device-tree action on this tab,
/// which pnputil covers), so this deliberately stops short of performing the rollback itself,
/// matching the suggestion text's own "opens the device's driver property sheet" framing.
///
/// Detection combines two independent, best-effort signals rather than one authoritative check
/// (CLAUDE.md's "quick flag, not a verdict" convention - Windows exposes no documented API/registry
/// value that just says "yes, a rollback is available for this device"):
///   1. %windir%\System32\ReinstallBackups\&lt;NNNN&gt; - the actual backup location Device Manager's
///      own "Roll Back Driver" reads from when a driver update replaced a previous version. NNNN is
///      the same 4-digit driver-node number as the device's own
///      Control\Class\{ClassGuid}\NNNN registry key (read via the device's Enum "Driver" value,
///      the same hop DriverInventoryService.ComputeMatchQuality and DriverStoreService.
///      ReadBoundInfName already make for #458/#484).
///   2. An older driver-store package (#480's staleness data) sharing this device's currently-bound
///      package's OriginalName/Provider - a rollback target existing in the store even when Windows
///      didn't keep a dedicated ReinstallBackups copy (e.g. the "previous" version was reinstalled
///      from the driver store rather than replaced in place).
/// Either signal alone is enough to show the action - a false positive here just means the "Roll
/// Back Driver" button turns out disabled on the property sheet the user already asked to open.
/// </summary>
public static class DriverRollbackService
{
    public readonly record struct RollbackAvailability(bool Available, string Reason);

    /// <summary>Checks both signals described above. driverStorePackages may be null/empty when
    /// the Driver store view hasn't been loaded yet this session - in that case only the
    /// ReinstallBackups signal is used, never treated as "definitely not available".</summary>
    public static RollbackAvailability Check(string deviceId, string? boundInfName, IReadOnlyList<DriverStorePackage>? driverStorePackages)
    {
        bool hasBackupFolder = HasReinstallBackup(deviceId, out string? nnnn);

        bool hasOlderStorePackage = false;
        if (driverStorePackages is { Count: > 0 } && !string.IsNullOrEmpty(boundInfName))
        {
            var current = driverStorePackages.FirstOrDefault(p => p.PublishedName.Equals(boundInfName, StringComparison.OrdinalIgnoreCase));
            if (current is not null)
            {
                hasOlderStorePackage = driverStorePackages.Any(p =>
                    !p.PublishedName.Equals(current.PublishedName, StringComparison.OrdinalIgnoreCase) &&
                    p.OriginalName.Equals(current.OriginalName, StringComparison.OrdinalIgnoreCase) &&
                    p.Provider.Equals(current.Provider, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!hasBackupFolder && !hasOlderStorePackage) return new RollbackAvailability(false, string.Empty);

        string reason = (hasBackupFolder, hasOlderStorePackage) switch
        {
            (true, true) => "A previous driver backup and an older driver store package were both found.",
            (true, false) => $"A previous driver backup was found (ReinstallBackups\\{nnnn}).",
            _ => "An older driver store package for this device's driver was found.",
        };
        return new RollbackAvailability(true, reason);
    }

    private static bool HasReinstallBackup(string deviceId, out string? nnnn)
    {
        nnnn = null;
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{deviceId}");
            string? driverRef = enumKey?.GetValue("Driver") as string; // "{classguid}\NNNN"
            if (string.IsNullOrEmpty(driverRef)) return false;

            int sep = driverRef.LastIndexOf('\\');
            if (sep < 0 || sep == driverRef.Length - 1) return false;
            nnnn = driverRef[(sep + 1)..];

            string backupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "ReinstallBackups", nnnn);
            return Directory.Exists(backupDir) && Directory.EnumerateFileSystemEntries(backupDir).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens Device Manager's driver-properties sheet directly on this device - the same
    /// devmgr.dll entry point Device Manager's own "Properties" context-menu item uses internally.
    /// Fire-and-forget, matching MemoryDiagnosticLauncherService's launch-and-return-immediately
    /// shape for a tool that shows its own UI.</summary>
    public static (bool Success, string? Error) OpenDeviceProperties(string deviceId)
    {
        try
        {
            string args = $"devmgr.dll,DeviceProperties_RunDLL /MachineName \"\" /DeviceID \"{deviceId}\"";
            var startInfo = new ProcessStartInfo("rundll32.exe", args) { UseShellExecute = true };
            using var process = Process.Start(startInfo);
            return process is not null ? (true, null) : (false, "rundll32.exe didn't start.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
