using System.Diagnostics;
using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// #472/#474/#475: device-tree actions (remove/enable/disable/restart) via pnputil.exe's
/// `/remove-device`, `/enable-device`, `/disable-device`, `/restart-device` verbs (added in
/// Windows 10 2004 and present on every Windows 11 build) - matches this app's "known Windows
/// tool over raw interop" convention (SetupDiCallClassInstaller with DIF_REMOVE/DIF_PROPERTYCHANGE
/// would be the raw-interop alternative; pnputil is simpler and is already the house style for
/// every other driver-package action on this tab). Uses the same concurrent-ReadToEndAsync +
/// bounded-WaitForExitAsync + Kill()-on-timeout pattern ScheduledTaskService/PowerPlanService
/// already establish - a longer 30s timeout than those since a device restart/removal can
/// legitimately take a little longer than a plain query.
/// </summary>
public static class PnpUtilService
{
    public static Task<(bool Success, string Message)> RemoveDeviceAsync(string deviceId) => RunAsync("remove-device", deviceId);
    public static Task<(bool Success, string Message)> EnableDeviceAsync(string deviceId) => RunAsync("enable-device", deviceId);
    public static Task<(bool Success, string Message)> DisableDeviceAsync(string deviceId) => RunAsync("disable-device", deviceId);
    public static Task<(bool Success, string Message)> RestartDeviceAsync(string deviceId) => RunAsync("restart-device", deviceId);

    /// <summary>#479: lists every package currently in the driver store (oemNN.inf) - the
    /// authoritative "what's actually installed" list DriverStoreService parses, distinct from
    /// #453's driverquery-anchored inventory (which lists *running kernel services*, not driver-
    /// store packages). Returns the raw combined stdout+stderr rather than a collapsed one-line
    /// Message like the verb actions below, since the caller needs the full text to parse. A
    /// longer 45s timeout - a driver store with several hundred packages genuinely takes a bit
    /// longer to enumerate than a single-device query.</summary>
    public static async Task<(bool Success, string Output)> EnumDriversAsync()
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("pnputil.exe", "/enum-drivers", timeoutMs: 45000);
            return (exitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>#481: deletes one driver-store package. `force` adds `/force`, only ever passed
    /// by a caller after this app's own second, more serious confirmation (see
    /// DevicesDriversViewModel.DeleteCheckedDriverPackagesAsync) - pnputil itself already refuses
    /// to delete a package still bound to a present device without it, and this app additionally
    /// refuses to even offer deletion for one (see #484's in-use mapping), so /force here is only
    /// ever reached for a package pnputil considers "in use" for some other reason (e.g. still
    /// referenced by a non-present/ghost device's driver node).</summary>
    public static Task<(bool Success, string Message)> DeleteDriverAsync(string publishedName, bool force) =>
        RunArgsAsync($"/delete-driver \"{publishedName}\" /uninstall{(force ? " /force" : string.Empty)}");

    /// <summary>#482: backs up every third-party driver package to a user-chosen folder. pnputil
    /// prints one line per package as it finishes, not a percentage, so there's no granular
    /// progress to parse - callers show an indeterminate busy state instead. A long 5-minute
    /// timeout, since exporting a large driver store to a slow (network/USB) destination can
    /// genuinely take a while.</summary>
    public static Task<(bool Success, string Message)> ExportDriversAsync(string destinationFolder) =>
        RunArgsAsync($"/export-driver * \"{destinationFolder}\"", timeoutMs: 300000);

    /// <summary>#485: installs every .inf found under sourceFolder (recursively, via /subdirs).
    /// Same long-timeout rationale as ExportDriversAsync above.</summary>
    public static Task<(bool Success, string Message)> AddDriverAsync(string sourceFolder) =>
        RunArgsAsync($"/add-driver \"{Path.Combine(sourceFolder, "*.inf")}\" /subdirs /install", timeoutMs: 300000);

    private static async Task<(bool Success, string Message)> RunAsync(string verb, string deviceId) =>
        await RunArgsAsync($"/{verb} \"{deviceId}\"");

    private static async Task<(bool Success, string Message)> RunArgsAsync(string args, int timeoutMs = 30000)
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("pnputil.exe", args, timeoutMs);
            string trimmed = output.Trim();
            bool success = exitCode == 0;
            return (success, trimmed.Length > 0 ? trimmed : (success ? "Done." : $"pnputil exited with code {exitCode}."));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's historical default timeout.</summary>
    private static Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 30000)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
}
