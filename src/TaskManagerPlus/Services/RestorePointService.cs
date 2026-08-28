using System.Management;

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
}
