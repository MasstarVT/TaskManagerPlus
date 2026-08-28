using TaskManagerPlus.Models;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Services;

/// <summary>
/// #974: evaluates a RemediationAction's declared <see cref="RemediationAction.Preconditions"/>
/// against live ViewModel state (the same ServicesViewModel/SystemSpecsViewModel instances
/// MainViewModel already composes) - never a new sampling source of its own. Cheap: every check
/// here either reads an already-polled/already-loaded collection directly, or (System Protection
/// only) makes one short-timeout PowerShell query - see RestorePointService.
/// CheckSystemProtectionEnabledAsync's remarks on why that one has to be async at all.
/// </summary>
public static class RemediationPreconditionService
{
    public static async Task<List<PreconditionCheckResult>> CheckAsync(RemediationAction action, ServicesViewModel services, SystemSpecsViewModel systemSpecs)
    {
        var results = new List<PreconditionCheckResult>();
        foreach (var pre in action.Preconditions)
            results.Add(await CheckOneAsync(pre, services, systemSpecs));
        return results;
    }

    private static async Task<PreconditionCheckResult> CheckOneAsync(RemediationPrecondition pre, ServicesViewModel services, SystemSpecsViewModel systemSpecs)
    {
        switch (pre.Kind)
        {
            case PreconditionKind.RequiresElevation:
            {
                // This app runs fully elevated (app.manifest -> requireAdministrator) for its
                // entire lifetime - MainViewModel.IsElevated already asserts this once at startup,
                // so this is a defensive re-check, not something expected to ever actually fail.
                bool elevated = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                return new PreconditionCheckResult
                {
                    Precondition = pre,
                    Passed = elevated,
                    Reason = elevated ? null : "This app isn't running elevated.",
                };
            }

            case PreconditionKind.RequiresServicePresent:
            {
                bool present = pre.Parameter is { Length: > 0 } name &&
                    services.Services.Any(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase));
                return new PreconditionCheckResult
                {
                    Precondition = pre,
                    Passed = present,
                    Reason = present ? null : $"The \"{pre.Parameter}\" service is no longer present on this system.",
                };
            }

            case PreconditionKind.RequiresNtfsVolume:
            {
                string? wanted = NormalizeDrive(pre.Parameter);
                var volume = systemSpecs.Volumes.FirstOrDefault(v => NormalizeDrive(v.Primary) == wanted);
                if (volume is null)
                {
                    // The volume itself couldn't be found this pass - degrade to "unknown", not a
                    // fabricated pass or fail.
                    return new PreconditionCheckResult { Precondition = pre, Passed = null, Reason = $"Couldn't read {pre.Parameter}'s file system." };
                }
                bool isNtfs = string.Equals(volume.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);
                return new PreconditionCheckResult
                {
                    Precondition = pre,
                    Passed = isNtfs,
                    Reason = isNtfs ? null : $"This fix is offered only for NTFS volumes - {pre.Parameter} is {volume.FileSystem}.",
                };
            }

            case PreconditionKind.RequiresSystemProtectionOn:
            {
                bool? enabled = await RestorePointService.CheckSystemProtectionEnabledAsync();
                return new PreconditionCheckResult
                {
                    Precondition = pre,
                    Passed = enabled,
                    Reason = enabled switch
                    {
                        false => "System Protection looks disabled - creating a restore point below will likely fail; you can still skip it and run anyway.",
                        null => "Couldn't determine whether System Protection is on.",
                        true => null,
                    },
                };
            }

            case PreconditionKind.RequiresNoRebootPending:
            {
                bool pending = systemSpecs.RebootPending;
                return new PreconditionCheckResult
                {
                    Precondition = pre,
                    Passed = !pending,
                    Reason = pending ? "A restart is already pending on this system - finish that restart first for reliable results." : null,
                };
            }

            default:
                return new PreconditionCheckResult { Precondition = pre, Passed = null, Reason = "Unknown precondition." };
        }
    }

    private static string? NormalizeDrive(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().TrimEnd('\\').TrimEnd(':').ToUpperInvariant() + ":";
}
