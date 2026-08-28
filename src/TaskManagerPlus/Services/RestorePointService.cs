using System.Diagnostics;
using System.Management;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, item 83: a minimal, create-only System Restore point helper for the guided Driver
/// Verifier wizard's first step. Full restore-point management (browsing/deleting existing points,
/// scheduling, disk-usage settings, ...) is a later chunk's own item (#98) - this is deliberately
/// just the one call the wizard needs before it changes system-wide driver verification, via the
/// documented `SystemRestore` WMI class's static CreateRestorePoint method (root\default namespace)
/// rather than shelling out to vssadmin, since vssadmin only manages Volume Shadow Copies directly
/// and has no equivalent "create a System Restore checkpoint" verb - CreateRestorePoint is the
/// same WMI method System Protection's own UI and PowerShell's Checkpoint-Computer cmdlet call.
///
/// Round 21, item 98: extended (not duplicated - see this chunk's own instructions) with
/// enumeration of the same `SystemRestore` WMI class's own instances (each restore point IS one
/// instance of the class, not a separate query), a "launch rstrui.exe" action for the actual
/// rollback (a full System Restore rollback needs to run outside the current session - Windows
/// itself only exposes that through rstrui.exe's own wizard, not a scriptable WMI method), and a
/// best-effort "is System Protection even turned on" flag - see
/// <see cref="ReadSystemProtectionStatus"/>'s own remarks on exactly how tentative that flag is.
///
/// #970/#974: a second, async create/check pair for the Troubleshoot tab's remediation-review flow
/// (<see cref="CreateRestorePointAsync"/>/<see cref="CheckSystemProtectionEnabledAsync"/>), shelling
/// out to PowerShell's Checkpoint-Computer/Get-ComputerRestorePoint cmdlets instead of the direct
/// WMI call above - Checkpoint-Computer is what those cmdlets call internally, so this is the
/// "known tool" version of the same call rather than a second, redundant WMI path, and its own
/// error text is what RemediationPreconditionService's advisory precheck and the review dialog's
/// "System Protection looks disabled" messaging depend on. Kept alongside the original sync/WMI
/// pair above (different method names, no collision) rather than merged into it, since callers on
/// each side depend on the specific shape/timing (sync WMI call for the Driver Verifier wizard and
/// Stability tab; async PowerShell call plus a disabled-detection flag for the remediation flow).
/// </summary>
public static class RestorePointService
{
    // Per MS-RSP/SystemRestore documentation: RestorePointType 12 = MODIFY_SETTINGS (a generic,
    // low-risk "something is about to change" checkpoint - the closest documented type to "about
    // to make a risky driver/registry change" without claiming a more specific category like
    // DEVICE_DRIVER_INSTALL that wouldn't be accurate here). EventType 100 = BEGIN_SYSTEM_CHANGE.
    private const uint RestorePointTypeModifySettings = 12;
    private const uint EventTypeBeginSystemChange = 100;

    /// <summary>Creates one System Restore point with the given description. Returns (false,
    /// reason) rather than throwing on any failure - System Restore can be off entirely (a common,
    /// expected state on many machines, especially non-system drives or Windows editions/policies
    /// that disable it), which this treats as a real, expected outcome per CLAUDE.md's "degrade to
    /// Unknown/hidden, never fabricate" rather than an error condition.</summary>
    public static (bool Ok, string Message) TryCreate(string description)
    {
        try
        {
            using var restoreClass = new ManagementClass(@"root\default", "SystemRestore", null);
            using var inParams = restoreClass.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description;
            inParams["RestorePointType"] = RestorePointTypeModifySettings;
            inParams["EventType"] = EventTypeBeginSystemChange;

            using var outParams = restoreClass.InvokeMethod("CreateRestorePoint", inParams, null);
            uint returnValue = outParams is null ? 0 : Convert.ToUInt32(outParams["ReturnValue"]);

            // CreateRestorePoint returns 0 on failure, 1 (ERROR_SUCCESS-equivalent) on success -
            // per the documented SystemRestore WMI class contract.
            return returnValue != 0
                ? (true, "System Restore point created.")
                : (false, "System Restore point could not be created - System Restore may be turned off for this drive, or the service isn't running.");
        }
        catch (Exception ex)
        {
            return (false, $"System Restore point could not be created: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Round 21, item 98: enumeration + System Protection status + rollback launch.
    // ---------------------------------------------------------------------------------------

    /// <summary>Item 98: every existing restore point plus a best-effort "is System Protection on
    /// at all" flag. Reading zero restore points is genuinely ambiguous on its own (System
    /// Protection could be on with no points created yet, or off entirely) - <see
    /// cref="ProtectionEnabled"/> combines that count with the documented DisableSR policy value
    /// and a Win32_ShadowStorage association check for the system volume to make a best-effort call
    /// either way, and degrades to null (unknown) rather than guessing when even that isn't
    /// conclusive. "Quick flag, not a verdict" per CLAUDE.md: there is no single documented WMI/
    /// registry API for "is System Protection on for this volume" the way there is for, say,
    /// BitLocker status.</summary>
    public static SystemProtectionStatus ReadSystemProtectionStatus()
    {
        var points = new List<RestorePointInfo>();
        bool readOk = true;
        string? errorText = null;

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT * FROM SystemRestore");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    uint sequence = mo["SequenceNumber"] is { } s ? Convert.ToUInt32(s) : 0;
                    uint type = mo["RestorePointType"] is { } t ? Convert.ToUInt32(t) : 0;
                    string description = mo["Description"] as string ?? string.Empty;
                    DateTime created = mo["CreationTime"] is string wmiDate && wmiDate.Length > 0
                        ? SafeParseWmiDate(wmiDate)
                        : DateTime.MinValue;

                    points.Add(new RestorePointInfo
                    {
                        SequenceNumber = (int)sequence,
                        CreationTime = created,
                        Description = description,
                        RestorePointTypeText = DescribeRestorePointType(type),
                    });
                }
            }
        }
        catch (Exception ex)
        {
            readOk = false;
            errorText = ex.Message;
        }

        points = points.OrderByDescending(p => p.CreationTime).ToList();

        bool? disabledByPolicy = ReadDisableSrPolicy();
        bool? shadowStorageOnSystemVolume = readOk ? IsShadowStorageAllocatedForSystemVolume() : null;

        bool? enabled;
        string statusText;
        if (disabledByPolicy == true)
        {
            enabled = false;
            statusText = "Off - System Restore is disabled by policy (DisableSR=1).";
        }
        else if (!readOk)
        {
            enabled = null;
            statusText = $"Unknown - couldn't read restore points ({errorText}).";
        }
        else if (points.Count > 0)
        {
            enabled = true;
            statusText = $"On - {points.Count} restore point{(points.Count == 1 ? "" : "s")} found.";
        }
        else if (shadowStorageOnSystemVolume == true)
        {
            enabled = true;
            statusText = "On - the system volume has shadow-copy storage allocated for it, though no restore points were found yet.";
        }
        else if (shadowStorageOnSystemVolume == false)
        {
            enabled = false;
            statusText = "Off (most likely) - no restore points were found and the system volume has no shadow-copy storage allocated. Worth confirming manually in System Protection settings before relying on this.";
        }
        else
        {
            enabled = null;
            statusText = "Unknown - no restore points were found, and this app couldn't confirm whether System Protection is turned on.";
        }

        return new SystemProtectionStatus
        {
            ReadOk = readOk,
            ErrorText = errorText,
            ProtectionEnabled = enabled,
            ProtectionStatusText = statusText,
            RestorePoints = points,
        };
    }

    /// <summary>Item 98: launches Windows' own System Restore wizard for the actual rollback - a
    /// full rollback has to run outside this app's own process (the machine reboots partway
    /// through), so rstrui.exe's interactive wizard is the only way to do it at all, scripted or
    /// not; this app only ever opens it, never drives it.</summary>
    public static bool LaunchRstrui()
    {
        try
        {
            Process.Start(new ProcessStartInfo("rstrui.exe") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeRestorePointType(uint type) => type switch
    {
        0 => "Application install",
        1 => "Application uninstall",
        6 => "Backup/recovery",
        7 => "Checkpoint (scheduled)",
        10 => "Device driver install",
        12 => "Modify settings",
        13 => "Cancelled operation",
        _ => $"Type {type}",
    };

    private static DateTime SafeParseWmiDate(string wmiDate)
    {
        try { return ManagementDateTimeConverter.ToDateTime(wmiDate); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>The documented Group-Policy-backed "Turn off System Restore" flag - 1 means System
    /// Restore is disabled outright, machine-wide, regardless of any per-volume toggle.</summary>
    private static bool? ReadDisableSrPolicy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            return key?.GetValue("DisableSR") is { } v ? Convert.ToInt32(v) != 0 : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Best-effort signal for "is the system volume enrolled in VSS shadow-copy storage" -
    /// enabling System Protection for a volume is what allocates this storage in the first place,
    /// so its presence (or absence) is a reasonable proxy for the toggle Windows itself doesn't
    /// otherwise expose a direct on/off read for. Null (not false) on any read failure - a WMI
    /// namespace/class that couldn't be queried at all says nothing about the real state.</summary>
    private static bool? IsShadowStorageAllocatedForSystemVolume()
    {
        try
        {
            string systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');

            string? systemVolumeDeviceId = null;
            using (var volumeSearcher = new ManagementObjectSearcher(
                $"SELECT DeviceID FROM Win32_Volume WHERE DriveLetter='{systemDrive}'"))
            {
                foreach (ManagementObject mo in volumeSearcher.Get())
                {
                    using (mo) { systemVolumeDeviceId = mo["DeviceID"] as string; }
                    break;
                }
            }
            if (string.IsNullOrEmpty(systemVolumeDeviceId)) return null;

            using var shadowSearcher = new ManagementObjectSearcher("SELECT Volume FROM Win32_ShadowStorage");
            foreach (ManagementObject mo in shadowSearcher.Get())
            {
                using (mo)
                {
                    string? volumeRef = mo["Volume"]?.ToString();
                    if (volumeRef is not null && volumeRef.Contains(systemVolumeDeviceId, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------
    // #970/#974: async PowerShell-based create/check pair for the Troubleshoot tab's
    // remediation-review flow - see the class summary above for why this coexists with the
    // sync/WMI pair above rather than being merged into it.
    // ---------------------------------------------------------------------------------------

    /// <summary>True when Checkpoint-Computer's own error output matches its well-known "System
    /// Restore is disabled" phrasing - used to swap in a clearer, actionable message instead of the
    /// raw PowerShell exception text.</summary>
    private static bool LooksLikeSystemProtectionDisabled(string output) =>
        output.Contains("System Restore is disabled", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("disabled on this drive", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("0x81045303", StringComparison.OrdinalIgnoreCase);

    public static async Task<(bool Success, string Message, bool SystemProtectionLikelyDisabled)> CreateRestorePointAsync(string description)
    {
        try
        {
            // Checkpoint-Computer throttles to one checkpoint per 24h by default for
            // MODIFY_SETTINGS-type points on client SKUs - reported the same way as any other
            // failure below rather than special-cased, since the message it returns already says
            // so plainly.
            string safeDescription = description.Replace("'", "''");
            string psCommand =
                $"$ErrorActionPreference='Stop'; try {{ Checkpoint-Computer -Description '{safeDescription}' -RestorePointType MODIFY_SETTINGS; Write-Output 'RESTORE_POINT_OK' }} catch {{ Write-Output $_.Exception.Message }}";

            var (output, exitCode) = await TroubleshootService.RunCapturedAsync(
                "powershell.exe", $"-NoProfile -NonInteractive -Command \"{psCommand.Replace("\"", "\\\"")}\"", timeoutMs: 60000);

            if (exitCode == 0 && output.Contains("RESTORE_POINT_OK", StringComparison.Ordinal))
                return (true, "Restore point created.", false);

            bool disabled = LooksLikeSystemProtectionDisabled(output);
            string message = disabled
                ? "Couldn't create a restore point - System Protection looks disabled for this drive."
                : $"Couldn't create a restore point: {(string.IsNullOrWhiteSpace(output) ? "unknown error" : output.Trim())}";
            return (false, message, disabled);
        }
        catch (Exception ex)
        {
            return (false, $"Couldn't create a restore point: {ex.Message}", false);
        }
    }

    /// <summary>#974: a read-only, non-mutating precheck of whether System Restore looks enabled -
    /// reused by RemediationPreconditionService's advisory RequiresSystemProtectionOn check so the
    /// review dialog can show this ahead of time rather than only after a failed
    /// CreateRestorePointAsync attempt. Deliberately calls Get-ComputerRestorePoint (a query) and
    /// never Enable-ComputerRestore - a precondition *check* must never have the side effect of
    /// actually turning System Protection on. Returns null when the check itself couldn't
    /// determine an answer either way (CLAUDE.md's "degrade to Unknown, never fabricate") -
    /// callers must not treat null as "enabled".</summary>
    public static async Task<bool?> CheckSystemProtectionEnabledAsync()
    {
        try
        {
            const string psCommand =
                "$ErrorActionPreference='Stop'; try { Get-ComputerRestorePoint | Out-Null; Write-Output 'SR_QUERY_OK' } catch { Write-Output $_.Exception.Message }";

            var (output, exitCode) = await TroubleshootService.RunCapturedAsync(
                "powershell.exe", $"-NoProfile -NonInteractive -Command \"{psCommand}\"", timeoutMs: 20000);

            if (exitCode is null) return null; // timed out - couldn't determine an answer
            if (output.Contains("SR_QUERY_OK", StringComparison.Ordinal)) return true;
            if (LooksLikeSystemProtectionDisabled(output)) return false;
            return null; // some other failure (PowerShell missing, WMI namespace denied, ...) - unknown, not "off"
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Opens the Windows "System Protection" settings tab directly - the fallback offered
    /// when CreateRestorePointAsync reports System Protection is disabled.</summary>
    public static void OpenSystemProtectionSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("SystemPropertiesProtection.exe") { UseShellExecute = true });
        }
        catch
        {
            // Best-effort - if even this can't launch, there's nothing more this app can do here.
        }
    }
}
