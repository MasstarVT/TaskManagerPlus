using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #497/#498/#499: Driver Verifier status query, the standard-settings setup action, and the two
/// recovery actions (verifier /reset, bcdedit safe-boot toggle). Every mutating action here
/// (EnableStandardAsync/ResetAsync/SetSafeBootAsync) is destructive-adjacent - see
/// DevicesDriversViewModel's remarks and DriverVerifierSetupWindow for the mandatory warning/typed-
/// confirmation UI CLAUDE.md's safety-critical callout requires in front of every call into this
/// class's mutating methods. This service itself does no confirmation of its own - it trusts the
/// caller already got explicit, informed consent.
/// </summary>
public static class DriverVerifierService
{
    private const string MemoryManagementKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    /// <summary>Microsoft's documented Driver Verifier flag bits (see "Driver Verifier flags" in
    /// the WDK docs) - a best-effort decode. A bit not in this table shows as a raw hex value
    /// instead of a guessed name, matching this app's degrade-never-fabricate convention.</summary>
    private static readonly (uint Flag, string Name)[] KnownFlags =
    {
        (0x00000001u, "Special pool"),
        (0x00000002u, "Pool tracking"),
        (0x00000004u, "Force IRQL checking"),
        (0x00000008u, "I/O verification"),
        (0x00000010u, "Deadlock detection"),
        (0x00000020u, "Enhanced I/O verification (DMA checking)"),
        (0x00000080u, "Systematic low-resource simulation"),
        (0x00000100u, "DDI compliance checking"),
        (0x00000800u, "Security checks"),
        (0x00002000u, "Miscellaneous checks"),
        (0x00008000u, "Force pending I/O requests"),
        (0x00020000u, "IRP logging"),
        (0x00080000u, "Systematic Low Resource Simulation (dynamically enabled)"),
    };

    // ------------------------------------------------------------------------------------------
    // #497: status.
    // ------------------------------------------------------------------------------------------

    public static async Task<DriverVerifierStatus> QueryStatusAsync()
    {
        var (isConfigured, verifiesAll, driverNames, levelRaw) = ReadConfiguredState();

        string queryOutput;
        string? queryError = null;
        try
        {
            queryOutput = await RunCapturedAsync("verifier.exe", "/query", timeoutMs: 15000);
        }
        catch (Exception ex)
        {
            queryOutput = string.Empty;
            queryError = ex.Message;
        }

        var activeDrivers = ParseActiveDrivers(queryOutput);

        return new DriverVerifierStatus
        {
            IsConfigured = isConfigured,
            VerifiesAllDrivers = verifiesAll,
            ConfiguredDriverNames = driverNames,
            VerifyLevelRaw = levelRaw,
            EnabledChecks = DescribeFlags(levelRaw),
            IsActiveThisBoot = activeDrivers.Count > 0,
            ActiveDriverNames = activeDrivers,
            QueryError = queryError,
        };
    }

    private static (bool IsConfigured, bool VerifiesAll, List<string> DriverNames, uint LevelRaw) ReadConfiguredState()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MemoryManagementKeyPath);
            if (key is null) return (false, false, new List<string>(), 0);

            string? verifyDrivers = key.GetValue("VerifyDrivers") as string;
            uint level = key.GetValue("VerifyDriverLevel") switch
            {
                int i => unchecked((uint)i),
                uint u => u,
                _ => 0u,
            };

            bool verifiesAll = verifyDrivers?.Trim() == "*";
            var names = string.IsNullOrWhiteSpace(verifyDrivers) || verifiesAll
                ? new List<string>()
                : verifyDrivers.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            bool isConfigured = verifiesAll || names.Count > 0;
            return (isConfigured, verifiesAll, names, level);
        }
        catch
        {
            return (false, false, new List<string>(), 0);
        }
    }

    private static List<string> DescribeFlags(uint level)
    {
        var descriptions = new List<string>();
        if (level == 0) return descriptions;

        uint remaining = level;
        foreach (var (flag, name) in KnownFlags)
        {
            if ((level & flag) == flag)
            {
                descriptions.Add(name);
                remaining &= ~flag;
            }
        }
        if (remaining != 0) descriptions.Add($"Unrecognized flag(s): 0x{remaining:X}");
        return descriptions;
    }

    /// <summary>`verifier /query` prints one ".sys (Description)" line per currently-verified
    /// driver under a "Verified drivers" heading, followed by a statistics section - this stops
    /// collecting names once it sees a line that looks like a statistics header rather than trying
    /// to parse the (much less stable) statistics table itself.</summary>
    private static List<string> ParseActiveDrivers(string output)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return names;
        if (output.Contains("No drivers", StringComparison.OrdinalIgnoreCase)) return names;

        var sysNameRegex = new Regex(@"^\s*([A-Za-z0-9_.\-]+\.sys)\b", RegexOptions.IgnoreCase);
        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Contains("STATISTICS", StringComparison.OrdinalIgnoreCase)) break;
            var m = sysNameRegex.Match(line);
            if (m.Success && !names.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                names.Add(m.Groups[1].Value);
        }
        return names;
    }

    // ------------------------------------------------------------------------------------------
    // #498: enable standard verification against the given driver file names.
    // ------------------------------------------------------------------------------------------

    public static Task<(bool Success, string Message)> EnableStandardAsync(IReadOnlyList<string> driverFileNames)
    {
        if (driverFileNames.Count == 0) return Task.FromResult((false, "No drivers selected."));
        string args = $"/standard /driver {string.Join(' ', driverFileNames)}";
        return RunArgsAsync("verifier.exe", args, timeoutMs: 30000);
    }

    // ------------------------------------------------------------------------------------------
    // #499: recovery - verifier /reset (needs a reboot to take effect) and the bcdedit safe-boot
    // toggle for when the machine is already bugchecking on every normal boot.
    // ------------------------------------------------------------------------------------------

    public static Task<(bool Success, string Message)> ResetAsync() =>
        RunArgsAsync("verifier.exe", "/reset", timeoutMs: 30000);

    /// <summary>Reads whether {current}'s BCD entry currently has a safeboot value set, and which
    /// mode - `bcdedit /enum {current}` only ever prints a "safeboot" line when one is configured.</summary>
    public static async Task<(bool IsConfigured, string? Mode)> QuerySafeBootAsync()
    {
        string output;
        try
        {
            output = await RunCapturedAsync("bcdedit.exe", "/enum {current}", timeoutMs: 15000);
        }
        catch
        {
            return (false, null);
        }

        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("safeboot", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                return (true, parts.Length > 1 ? parts[1].Trim() : null);
            }
        }
        return (false, null);
    }

    public static Task<(bool Success, string Message)> SetSafeBootAsync(bool enable) => enable
        ? RunArgsAsync("bcdedit.exe", "/set {current} safeboot minimal", timeoutMs: 15000)
        : RunArgsAsync("bcdedit.exe", "/deletevalue {current} safeboot", timeoutMs: 15000);

    // ------------------------------------------------------------------------------------------

    private static async Task<(bool Success, string Message)> RunArgsAsync(string exe, string args, int timeoutMs)
    {
        try
        {
            string output = await RunCapturedAsync(exe, args, timeoutMs);
            return (true, output.Trim().Length > 0 ? output.Trim() : "Done.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Same concurrent-read/bounded-wait/kill-on-timeout shape PnpUtilService already
    /// establishes - duplicated here rather than shared, matching this app's existing
    /// self-contained-service convention.</summary>
    private static async Task<string> RunCapturedAsync(string exe, string args, int timeoutMs)
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
            return "(command timed out)";
        }

        return (await outputTask) + (await errorTask);
    }
}
