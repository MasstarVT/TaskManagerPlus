using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #789/#790: System Restore inventory and creation - the WMI SystemRestore class (root\default,
/// not the usual root\cimv2 every other WMI read in this app targets) for the actual restore-point
/// list and for creating a new one, `vssadmin list shadowstorage` for per-volume shadow-storage
/// allocation (the same known-tool tradeoff VolumeDiagnosticsService.ReadShadowCopyUsageByVolumeAsync
/// already takes for the Storage tab's own Used-only reading - this file keeps its own small parser
/// rather than sharing that one, since #789 needs Allocated/Maximum alongside Used), and the
/// documented SystemRestorePointCreationFrequency policy value that throttles Windows' own
/// *automatic* restore points to once per 24 hours by default.
/// </summary>
public static class SystemRestoreService
{
    private const string RestoreConfigKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";

    #region #789 - Restore point inventory

    public static async Task<SystemRestoreSnapshot> ReadSnapshotAsync()
    {
        var (points, available, error) = await Task.Run(ReadRestorePoints).ConfigureAwait(false);
        var shadowStorage = await ReadShadowStorageAsync().ConfigureAwait(false);
        var volumeProtection = BuildVolumeProtection(shadowStorage);
        int? frequency = ReadAutomaticFrequencyMinutes();

        return new SystemRestoreSnapshot
        {
            SystemRestoreAvailable = available,
            RestorePoints = points,
            VolumeProtection = volumeProtection,
            ShadowStorage = shadowStorage,
            AutomaticFrequencyMinutes = frequency,
            ErrorText = error,
        };
    }

    /// <summary>#789: SELECT * FROM SystemRestore in root\default - not reachable through the
    /// plain `new ManagementObjectSearcher("SELECT ...")` overload every other WMI read in this app
    /// uses (that overload implicitly targets root\cimv2), so this connects an explicit
    /// ManagementScope first. A ManagementException here (class/namespace not found) means System
    /// Restore itself isn't installed (some Server SKUs) - distinguished from "installed but zero
    /// points" via SystemRestoreAvailable/HasNoRestorePointsAtAll.</summary>
    private static (List<RestorePointInfo> Points, bool Available, string? Error) ReadRestorePoints()
    {
        var points = new List<RestorePointInfo>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\default");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM SystemRestore"));
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    int seq = mo["SequenceNumber"] is { } s ? Convert.ToInt32(s) : 0;
                    string desc = mo["Description"] as string ?? string.Empty;
                    uint type = mo["RestorePointType"] is { } t ? Convert.ToUInt32(t) : 0;
                    DateTime? created = null;
                    if (mo["CreationTime"] is string wmiDate)
                    {
                        try { created = ManagementDateTimeConverter.ToDateTime(wmiDate); }
                        catch { /* leave null */ }
                    }
                    points.Add(new RestorePointInfo
                    {
                        SequenceNumber = seq,
                        Description = desc,
                        RestorePointTypeText = DescribeRestorePointType(type),
                        CreationTime = created,
                    });
                }
            }
            return (points.OrderByDescending(p => p.CreationTime ?? DateTime.MinValue).ToList(), true, null);
        }
        catch (Exception ex)
        {
            return (points, false, ex.Message);
        }
    }

    private static string DescribeRestorePointType(uint type) => type switch
    {
        0 => "Application install",
        1 => "Application uninstall",
        10 => "Device driver install",
        12 => "Modify settings",
        13 => "Cancelled operation",
        14 => "Backup/recovery",
        _ => $"Type {type}",
    };

    #endregion

    #region #789 - shadow storage + per-volume protection inference

    private static readonly Regex ForVolumeRegex = new(@"For volume:.*\(([A-Za-z]):\)", RegexOptions.Compiled);
    private static readonly Regex UsedRegex = new(@"Used Shadow Copy Storage space:\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AllocatedRegex = new(@"Allocated Shadow Copy Storage space:\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MaxRegex = new(@"Maximum Shadow Copy Storage space:\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static async Task<List<RecoveryShadowStorageInfo>> ReadShadowStorageAsync()
    {
        var result = new List<RecoveryShadowStorageInfo>();
        try
        {
            var (output, _) = await RunCapturedAsync("vssadmin.exe", "list shadowstorage").ConfigureAwait(false);

            string? currentVolume = null;
            string? used = null, allocated = null, max = null;

            void Flush()
            {
                if (currentVolume is not null)
                    result.Add(new RecoveryShadowStorageInfo { Volume = currentVolume, UsedText = used ?? "Unknown", AllocatedText = allocated ?? "Unknown", MaxText = max ?? "Unknown" });
                used = allocated = max = null;
            }

            foreach (var raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();
                var volMatch = ForVolumeRegex.Match(line);
                if (volMatch.Success) { Flush(); currentVolume = volMatch.Groups[1].Value.ToUpperInvariant() + ":"; continue; }

                var usedMatch = UsedRegex.Match(line);
                if (usedMatch.Success) { used = usedMatch.Groups[1].Value.Trim(); continue; }
                var allocMatch = AllocatedRegex.Match(line);
                if (allocMatch.Success) { allocated = allocMatch.Groups[1].Value.Trim(); continue; }
                var maxMatch = MaxRegex.Match(line);
                if (maxMatch.Success) { max = maxMatch.Groups[1].Value.Trim(); continue; }
            }
            Flush();
        }
        catch
        {
            // vssadmin unavailable, or (very common) no volume has shadow storage configured at all.
        }
        return result;
    }

    /// <summary>#789: "System Protection state per volume" - Windows exposes no simple documented
    /// flag for this outside the System Properties UI's own internal check, so this infers it from
    /// whether vssadmin reports a shadow-storage association for the volume (System Restore relies
    /// on VSS to hold its snapshots there) - a quick flag, not a verdict, same as every other
    /// inferred heuristic in this app: a volume can show "no association" simply because no restore
    /// point has been taken on it yet, not necessarily because protection itself is off.</summary>
    private static List<VolumeProtectionStatus> BuildVolumeProtection(List<RecoveryShadowStorageInfo> shadowStorage)
    {
        var result = new List<VolumeProtectionStatus>();
        var withStorage = new HashSet<string>(shadowStorage.Select(s => s.Volume), StringComparer.OrdinalIgnoreCase);

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            string name;
            try
            {
                if (!drive.IsReady) continue;
                name = drive.Name.TrimEnd('\\');
            }
            catch { continue; }

            bool hasStorage = withStorage.Contains(name);
            result.Add(new VolumeProtectionStatus
            {
                Volume = name,
                ProtectionLooksOn = hasStorage ? true : null,
                Detail = hasStorage
                    ? "Has shadow-copy storage allocated - System Protection appears to be on for this volume."
                    : "No shadow-copy storage association found - either System Protection is off for this volume, or no restore point has been created on it yet.",
            });
        }
        return result;
    }

    #endregion

    #region #790 - Create restore point / rstrui launcher / automatic-frequency policy

    /// <summary>#790: SystemRestore.CreateRestorePoint(Description, RestorePointType, EventType) -
    /// the documented WMI method behind Windows' own "Create a restore point" button.
    /// RestorePointType 12 = MODIFY_SETTINGS (this chunk's spec); EventType 100 =
    /// BEGIN_SYSTEM_CHANGE. Windows still applies its own constraints (System Protection must be on
    /// for the system volume; on some builds, calls in very quick succession get coalesced) - this
    /// reports the method's own return code/message rather than assuming success, the same
    /// "trust the tool's own verdict" tradeoff the DISM/SFC calls elsewhere in this chunk take.</summary>
    public static async Task<(bool Success, string Message)> CreateRestorePointAsync(string description)
    {
        return await Task.Run(() =>
        {
            try
            {
                uint? newestBefore = ReadNewestSequenceNumber();

                using var restoreClass = new ManagementClass(@"\\.\root\default:SystemRestore");
                using var inParams = restoreClass.GetMethodParameters("CreateRestorePoint");
                inParams["Description"] = description;
                inParams["RestorePointType"] = 12; // MODIFY_SETTINGS
                inParams["EventType"] = 100; // BEGIN_SYSTEM_CHANGE

                using var outParams = restoreClass.InvokeMethod("CreateRestorePoint", inParams, null);
                uint returnValue = outParams?["ReturnValue"] is { } rv ? Convert.ToUInt32(rv) : 1;
                if (returnValue != 0)
                    return (false, $"CreateRestorePoint returned code {returnValue} - System Protection may be off for the system volume.");

                // Windows returns success but silently skips the point when one was already created
                // inside the SystemRestorePointCreationFrequency window (24h by default) - only a
                // new, higher sequence number shows a point was really made.
                return CheckPointWasCreated(newestBefore) ?? (true, "Restore point created.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>After a CreateRestorePoint call that returned 0: null when a new point really
    /// exists, else the failure to report. A sequence number that didn't advance, or no point at
    /// all afterwards, means Windows skipped it.</summary>
    internal static (bool Success, string Message)? CheckPointWasCreated(uint? newestBefore)
    {
        uint? newestAfter = ReadNewestSequenceNumber();
        if (newestAfter is { } after && (newestBefore is not { } before || after > before)) return null;

        if (newestAfter is null)
            return (false, "Windows reported success, but no restore point could be found afterwards - System Protection may be off for the system volume.");

        int windowMinutes = ReadAutomaticFrequencyMinutes() is int m and >= 0 ? m : 1440;
        if (windowMinutes == 0)
            return (false, "Windows reported success, but no new restore point was made. The existing points are unchanged.");
        string window = windowMinutes % 60 == 0 ? $"{windowMinutes / 60} hour(s)" : $"{windowMinutes} minute(s)";
        return (false, $"Windows skipped it - a restore point was already created within the last {window} (the SystemRestorePointCreationFrequency throttle), so no new point was made. The existing point is unchanged.");
    }

    /// <summary>Highest SystemRestore.SequenceNumber, or null when there are no points or the read
    /// fails.</summary>
    internal static uint? ReadNewestSequenceNumber()
    {
        try
        {
            uint? newest = null;
            using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT SequenceNumber FROM SystemRestore");
            foreach (ManagementObject rp in searcher.Get())
            {
                using (rp)
                {
                    if (rp["SequenceNumber"] is { } seq)
                    {
                        uint n = Convert.ToUInt32(seq);
                        if (newest is null || n > newest) newest = n;
                    }
                }
            }
            return newest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>#790: launches the built-in System Restore wizard - same UseShellExecute=true GUI
    /// launch pattern already used elsewhere in this app (wpa.exe/notepad.exe/explorer.exe).</summary>
    public static void LaunchRstrui()
    {
        try
        {
            Process.Start(new ProcessStartInfo("rstrui.exe") { UseShellExecute = true });
        }
        catch
        {
            // Best-effort - if rstrui.exe can't be launched (unusual), there's nothing more to do here.
        }
    }

    /// <summary>#790: SystemRestorePointCreationFrequency (minutes) - the documented policy value
    /// that throttles Windows' own *automatic* restore-point creation to once per this many minutes
    /// (Windows' own observed default, when the value is absent, is 1440 = 24 hours). Informational
    /// only - this chunk never writes this value.</summary>
    public static int? ReadAutomaticFrequencyMinutes()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RestoreConfigKeyPath);
            return key?.GetValue("SystemRestorePointCreationFrequency") is int v ? v : null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's historical non-nullable shape (-1 for a
    /// timed-out run).</summary>
    private static async Task<(string Output, int ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 15000)
    {
        var (output, exitCode) = await ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
        return (output, exitCode ?? -1);
    }
}
