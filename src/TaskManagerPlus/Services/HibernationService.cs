using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #658: hibernation configuration - whether it's enabled (`powercfg /a`'s own available-sleep-
/// states report, the same report PowerPlanService.ReadSleepStateSupportAsync already reads for a
/// different purpose - "Hibernate" listed under "available" means on) cross-checked against
/// HibernateEnabled under HKLM\SYSTEM\CurrentControlSet\Control\Power, plus the on-disk hiberfil.sys
/// size and whether HiberFileSizePercent (that same key) marks it reduced. Also #659's Fast Startup
/// (HiberbootEnabled) detection - the same registry key, so both live in one focused service rather
/// than two near-duplicate registry reads. `powercfg /a`'s "available sleep states" section format
/// was confirmed live on a real dev machine (each available state on its own line, e.g. "Standby
/// (S3)" / "Hibernate" / "Fast Startup", under a "The following sleep states are available on this
/// system:" header).
/// </summary>
public static class HibernationService
{
    private const string PowerKeyPath = @"SYSTEM\CurrentControlSet\Control\Power";

    public static async Task<HibernationStatus> ReadStatusAsync()
    {
        bool? enabledFromReport = null;
        try
        {
            var (output, _) = await RunProcessAsync("powercfg.exe", "/a", 10000);
            enabledFromReport = ParseHibernateAvailability(output);
        }
        catch { /* leave null - fall back to the registry flag alone below */ }

        bool? enabledFromRegistry = ReadDwordAsBool(PowerKeyPath, "HibernateEnabled");
        bool? enabled = enabledFromReport ?? enabledFromRegistry;

        int? sizePercent = ReadDword(PowerKeyPath, "HiberFileSizePercent");

        long? hiberfilSize = null;
        try
        {
            string sysDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
            string path = Path.Combine(sysDrive, "hiberfil.sys");
            if (File.Exists(path)) hiberfilSize = new FileInfo(path).Length;
        }
        catch { /* access denied/not found - leave Unknown, never a guessed size */ }

        bool isReduced = sizePercent is > 0 and < 100;
        string statusText = enabled switch
        {
            true when hiberfilSize is > 0 =>
                $"Hibernation is enabled ({(isReduced ? $"reduced, ~{sizePercent}% of RAM" : "full-size")} hiberfil.sys, {Formatting.FormatBytes(hiberfilSize.Value)}).",
            true => "Hibernation is enabled, but hiberfil.sys wasn't found or couldn't be read.",
            false => "Hibernation is disabled on this system.",
            null => "Couldn't determine hibernation status.",
        };

        return new HibernationStatus
        {
            Enabled = enabled,
            HiberfilSizeBytes = hiberfilSize,
            ConfiguredSizePercent = sizePercent,
            StatusText = statusText,
        };
    }

    /// <summary>Looks for "Hibernate" as its own line within the "available on this system" section
    /// of `powercfg /a`'s report - null when that section itself can't be located (an unrecognized
    /// report format on this Windows build), distinct from a confirmed "not available".</summary>
    internal static bool? ParseHibernateAvailability(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        int availIdx = output.IndexOf("available on this system:", StringComparison.OrdinalIgnoreCase);
        if (availIdx < 0) return null;

        int notAvailIdx = output.IndexOf("not available on this system:", StringComparison.OrdinalIgnoreCase);
        string availableSection = notAvailIdx > availIdx ? output[availIdx..notAvailIdx] : output[availIdx..];

        return Regex.IsMatch(availableSection, @"^\s*Hibernate\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    }

    /// <summary>Enable/disable hibernation via `powercfg /hibernate on|off` - needs administrator
    /// privileges (this app runs elevated throughout, per CLAUDE.md's elevation note).</summary>
    public static async Task<(bool Success, string? Error)> SetHibernationEnabledAsync(bool enabled)
    {
        try
        {
            var (output, exitCode) = await RunProcessAsync("powercfg.exe", enabled ? "/hibernate on" : "/hibernate off", 15000);
            return exitCode == 0 ? (true, null) : (false, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>#659: Fast Startup ("hybrid boot") - HiberbootEnabled under the same registry key.
    /// Null when the value isn't present (older Windows without Fast Startup at all, or hibernation
    /// itself unsupported/off) rather than assumed off.</summary>
    public static bool? ReadFastStartupEnabled() => ReadDwordAsBool(PowerKeyPath, "HiberbootEnabled");

    private static bool? ReadDwordAsBool(string subKey, string valueName) => ReadDword(subKey, valueName) is { } v ? v != 0 : null;

    private static int? ReadDword(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(valueName) is int v ? v : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Shells out and captures combined stdout+stderr, bounded by a real timeout - same
    /// concurrent-read/bounded-wait/kill-on-timeout pattern PowerPlanService.RunCapturedAsync
    /// established. Duplicated here rather than shared, matching this app's existing convention of
    /// each shelled-out-tool service owning its own small copy.</summary>
    private static async Task<(string Output, int? ExitCode)> RunProcessAsync(string exe, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { /* best-effort */ }
            return ("(command timed out)", null);
        }

        string output = (await outputTask) + (await errorTask);
        return (output, proc.ExitCode);
    }
}
