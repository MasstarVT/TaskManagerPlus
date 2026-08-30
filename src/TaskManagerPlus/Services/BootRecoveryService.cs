using System.Diagnostics;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 21, items 96/97: "reboot into Safe Mode/WinRE" plus the "boot configuration audit" that
/// tells the user what state the current boot entry is actually in before (and after) they do
/// that - both read/write the same `bcdedit {current}` boot entry, so they share one service and
/// one parse of `bcdedit /enum {current}`. Every mutating method here (the two reboot actions and
/// the revert) is a real, disruptive system change - per this chunk's own instructions, the UI is
/// responsible for the strongly-worded confirmation before calling any of them, the same
/// "service only ever performs the action once asked to" split every other mutating service in
/// this domain (DriverVerifierService, CrashDumpConfigService, ForcedCrashService) already uses.
/// Follows CLAUDE.md's "prefer a known Windows tool" convention throughout: bcdedit.exe and
/// shutdown.exe, not raw boot-configuration-data COM/WMI APIs.
/// </summary>
public static class BootRecoveryService
{
    // ---------------------------------------------------------------------------------------
    // Item 97: the fixed, documented set of flags this audit looks for - in the order they're
    // shown. Each is parsed independently out of one `bcdedit /enum {current}` text capture
    // rather than six separate shell-outs.
    // ---------------------------------------------------------------------------------------
    private static readonly (string Name, string Description)[] AuditedFlagNames =
    {
        ("safeboot", "Boots straight into Safe Mode every time, until reverted"),
        ("testsigning", "Allows test-signed (not properly signed) drivers to load"),
        ("nointegritychecks", "Disables boot-time driver signature/integrity verification entirely"),
        ("debug", "Kernel debugging is enabled - a debugger can attach and inspect/modify kernel memory"),
        ("bootstatuspolicy", "Controls whether a failed boot is shown/reported, and whether WinRE launches automatically"),
        ("hypervisorlaunchtype", "Controls whether the hypervisor (needed for Memory Integrity/Credential Guard) starts at boot"),
    };

    /// <summary>Items 96/97: runs `bcdedit /enum {current}` once and decodes every audited flag out
    /// of the same text capture - shared by item 96's "am I already in Safe Mode" check and item
    /// 97's plain-English audit list.</summary>
    public static async Task<BootConfigAudit> ReadBootConfigAuditAsync()
    {
        var (output, exitCode) = await RunCapturedAsync("bcdedit.exe", "/enum {current}");
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return new BootConfigAudit
            {
                ReadOk = false,
                ErrorText = exitCode is null
                    ? "Couldn't run bcdedit.exe."
                    : $"bcdedit /enum {{current}} failed (exit code {exitCode}).",
                RawOutput = output,
            };
        }

        var flags = new List<BootConfigFlag>();
        foreach (var (name, description) in AuditedFlagNames)
        {
            var match = Regex.Match(output, $@"(?im)^\s*{Regex.Escape(name)}\s+(.+?)\s*$");
            string raw = match.Success ? match.Groups[1].Value.Trim() : string.Empty;
            flags.Add(new BootConfigFlag
            {
                Name = name,
                RawValue = raw,
                PlainEnglish = DescribeFlag(name, raw, description),
                IsWarning = IsWarningValue(name, raw),
            });
        }

        return new BootConfigAudit { ReadOk = true, Flags = flags, RawOutput = output };
    }

    private static string DescribeFlag(string name, string raw, string description)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return name switch
            {
                "safeboot" => "Not set - normal boot (not Safe Mode).",
                "testsigning" => "Not set - Off (the normal, fully-enforced default).",
                "nointegritychecks" => "Not set - Off (the normal, fully-enforced default).",
                "debug" => "Not set - Off (kernel debugging is not enabled).",
                "bootstatuspolicy" => "Not set - Windows' own default applies (boot failures are reported, and WinRE launches after repeated failures).",
                "hypervisorlaunchtype" => "Not set - the hypervisor starts automatically only if a feature that needs it (e.g. Memory Integrity, Credential Guard) turned it on.",
                _ => "Not set.",
            };
        }

        return name switch
        {
            "safeboot" => raw.ToLowerInvariant() switch
            {
                "minimal" => "Safe Mode (minimal, no networking) - " + description,
                "network" => "Safe Mode with Networking - " + description,
                "dsrepair" => "Directory Services Repair Mode - " + description,
                _ => $"{raw} - {description}",
            },
            "testsigning" => DescribeYesNoFlag(raw, description),
            "nointegritychecks" => DescribeYesNoFlag(raw, description),
            "debug" => DescribeYesNoFlag(raw, description),
            "bootstatuspolicy" => raw.ToLowerInvariant() switch
            {
                "ignoreallfailures" => "IgnoreAllFailures - failed boots are never reported and WinRE never launches automatically. " + description,
                "ignorebootfailures" => "IgnoreBootFailures - boot failures aren't reported and don't trigger WinRE. " + description,
                "ignoreshutdownfailures" => "IgnoreShutdownFailures - unclean shutdowns aren't reported. " + description,
                "displayallfailures" => "DisplayAllFailures (the safe, reporting-everything setting).",
                _ => $"{raw} - {description}",
            },
            "hypervisorlaunchtype" => raw.Equals("Off", StringComparison.OrdinalIgnoreCase)
                ? "Off - the hypervisor won't start, so Memory Integrity/Credential Guard (if configured) can't actually run. " + description
                : $"{raw}.",
            _ => raw,
        };
    }

    /// <summary>#1055: bcdedit renders its boolean elements as "Yes"/"No" on English Windows, but a
    /// localized MUI install can render the value cell in its own language (e.g. "Ja"/"Oui"). Only
    /// an exact Yes/No is decoded; any other value degrades to "set, but unrecognized" - and is
    /// flagged as a warning by <see cref="IsWarningValue"/> - rather than fabricating a definitive
    /// Off for a flag that may well be On.</summary>
    private static string DescribeYesNoFlag(string raw, string description)
    {
        if (raw.Equals("Yes", StringComparison.OrdinalIgnoreCase)) return "On - " + description;
        if (raw.Equals("No", StringComparison.OrdinalIgnoreCase)) return "Off";
        return $"Set to \"{raw}\" - unrecognized value (possibly localized bcdedit output), so this flag may be On. {description}";
    }

    private static bool IsWarningValue(string name, string raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        return name switch
        {
            "safeboot" => true,
            // #1055: for the Yes/No flags, warn on anything that is set and isn't an explicit
            // "No" - an unrecognized (possibly localized) value must not read as a clean Off.
            "testsigning" => !raw.Equals("No", StringComparison.OrdinalIgnoreCase),
            "nointegritychecks" => !raw.Equals("No", StringComparison.OrdinalIgnoreCase),
            "debug" => !raw.Equals("No", StringComparison.OrdinalIgnoreCase),
            "bootstatuspolicy" => raw.StartsWith("Ignore", StringComparison.OrdinalIgnoreCase),
            "hypervisorlaunchtype" => raw.Equals("Off", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    // ---------------------------------------------------------------------------------------
    // Item 96: reboot into Safe Mode / WinRE, and the matching revert - every one of these
    // immediately restarts the machine (shutdown /t 0), so the UI's confirmation is the only
    // thing standing between a click and a restart; this service performs the action outright
    // once called, no further gate here.
    // ---------------------------------------------------------------------------------------

    /// <summary>Sets the current boot entry's safeboot value ("minimal" or "network") then
    /// restarts immediately. The machine comes back up in Safe Mode on every subsequent boot too,
    /// until <see cref="RevertSafeModeBootAsync"/> is run - that's the whole point (a hung/broken
    /// driver often can't even be reached long enough to fix from a normal boot), but it's also why
    /// the confirmation this method's caller shows must say so explicitly.</summary>
    public static async Task<(bool Ok, string Message)> RebootToSafeModeAsync(bool withNetworking)
    {
        string mode = withNetworking ? "network" : "minimal";
        var (setOutput, setExit) = await RunCapturedAsync("bcdedit.exe", $"/set {{current}} safeboot {mode}");
        if (setExit != 0)
            return (false, $"Couldn't set the Safe Mode boot flag: {setOutput.Trim()}");

        // shutdown.exe restarts the machine almost immediately - a null/nonzero exit code here
        // usually just means the process itself couldn't be read back in time, not that the
        // restart didn't happen; the bcdedit write above is what actually matters and already
        // succeeded, so this is reported as best-effort success rather than re-checked.
        await RunCapturedAsync("shutdown.exe", "/r /t 0", timeoutMs: 5000);
        return (true, $"Safe Mode boot flag set ({(withNetworking ? "with networking" : "minimal")}). Restarting now.");
    }

    /// <summary>Item 96: `shutdown /r /o /t 0` - restarts directly into the Advanced Startup
    /// Options / Windows Recovery Environment menu, Windows' own "/o" (options) switch, rather than
    /// setting any boot entry flag - a one-time trip, not a standing configuration like Safe Mode
    /// above.</summary>
    public static async Task<(bool Ok, string Message)> RebootToRecoveryEnvironmentAsync()
    {
        var (output, exitCode) = await RunCapturedAsync("shutdown.exe", "/r /o /t 0", timeoutMs: 5000);
        return exitCode is 0 or null
            ? (true, "Restarting into the Recovery Environment now.")
            : (false, $"Couldn't start the restart: {output.Trim()}");
    }

    /// <summary>Item 96's matching escape hatch: clears the safeboot value entirely, so the next
    /// boot (and every one after it) is a normal boot again. Does not itself restart the
    /// machine - the whole point is this can be run once the user is already back at a desktop,
    /// in or out of Safe Mode, ahead of the next normal restart.</summary>
    public static async Task<(bool Ok, string Message)> RevertSafeModeBootAsync()
    {
        var (output, exitCode) = await RunCapturedAsync("bcdedit.exe", "/deletevalue {current} safeboot");
        // bcdedit returns a nonzero exit code when the value wasn't set at all - not a real
        // failure, just "there was nothing to revert."
        if (exitCode == 0 || output.Contains("element not found", StringComparison.OrdinalIgnoreCase))
            return (true, "Safe Mode boot flag cleared - the next restart will be a normal boot.");
        return (false, $"Couldn't clear the Safe Mode boot flag: {output.Trim()}");
    }

    /// <summary>#1084: delegates to the shared <see cref="ToolRunner"/>, keeping this service's
    /// soft-start degradation (a tool that can't start yields an empty result, never a throw).</summary>
    private static async Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 15000)
    {
        try { return await ToolRunner.RunCapturedAsync(exe, args, timeoutMs); }
        catch { return (string.Empty, null); }
    }
}
