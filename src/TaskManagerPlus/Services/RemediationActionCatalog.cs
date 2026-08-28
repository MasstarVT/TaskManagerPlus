using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Services;

/// <summary>
/// #967: the fix-action library - every entry here wraps a REAL operation this app can already
/// perform, either a known Windows tool shelled out through TroubleshootService.RunCapturedAsync
/// (sfc, DISM, netsh, chkdsk, powercfg - CLAUDE.md's "known tool over raw interop" convention) or
/// one of this app's own existing Services/*.cs mutation methods (ServiceControlService.Restart,
/// StartupManagerService.SetEnabled, ProcessControlService.SetPriority) - never a placeholder.
///
/// A handful of ids need runtime context this static catalog can't know ahead of time (which
/// drive, which failed service, which startup item) - those are built via a parameterized factory
/// method rather than a fixed instance, and <see cref="Resolve"/> is what turns a fired
/// HealthIssue's declared <see cref="Rule.ActionIds"/> into concrete, ready-to-review
/// RemediationAction instances, pulling whatever context it can from that issue's own
/// resolved Message/Evidence or from live ViewModel state (e.g. "which service is currently
/// failed" from ServicesViewModel).
/// </summary>
public static class RemediationActionCatalog
{
    // ----- system-wide actions (no runtime target needed) --------------------------------------

    public static RemediationAction SfcScan() => new()
    {
        Id = "system.sfc-scannow",
        Title = "Repair protected system files (SFC)",
        PlainEnglishDescription = "Runs Windows' System File Checker, which scans every protected system file and automatically repairs any that don't match their known-good version. Can take several minutes; no reboot needed.",
        Command = "sfc /scannow",
        RiskLevel = RemediationRiskLevel.Medium,
        RequiresReboot = false,
        IsUndoable = false,
        NotUndoableReason = "One-shot repair tool run - there's nothing to reverse.",
        PreviewCommand = "sfc /verifyonly",
        ExecutePreview = _ => RunToolAsync("sfc.exe", "/verifyonly", timeoutMs: 1_800_000),
        Execute = _ => RunToolAsync("sfc.exe", "/scannow", timeoutMs: 1_800_000),
    };

    public static RemediationAction DismRestoreHealth() => new()
    {
        Id = "system.dism-restorehealth",
        Title = "Repair the Windows component store (DISM)",
        PlainEnglishDescription = "Runs DISM's online image repair, which downloads replacement files from Windows Update for any corrupted component-store files it finds - the usual next step when SFC alone can't fix something. Can take several minutes and needs an internet connection.",
        Command = "DISM /Online /Cleanup-Image /RestoreHealth",
        RiskLevel = RemediationRiskLevel.Medium,
        RequiresReboot = false,
        IsUndoable = false,
        NotUndoableReason = "One-shot repair tool run - there's nothing to reverse.",
        PreviewCommand = "DISM /Online /Cleanup-Image /ScanHealth",
        ExecutePreview = _ => RunToolAsync("DISM.exe", "/Online /Cleanup-Image /ScanHealth", timeoutMs: 900_000),
        Execute = _ => RunToolAsync("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", timeoutMs: 1_800_000),
    };

    public static RemediationAction NetshResetTcpIp() => new()
    {
        Id = "network.reset-tcpip",
        Title = "Reset the TCP/IP stack",
        PlainEnglishDescription = "Resets Windows' TCP/IP stack to its default configuration - can clear up persistent adapter-level network errors, but also clears any custom IP/proxy/Winsock settings you've configured. A restart is recommended afterward for the reset to fully take effect.",
        Command = "netsh int ip reset",
        RiskLevel = RemediationRiskLevel.High,
        RequiresReboot = true,
        IsUndoable = false,
        NotUndoableReason = "Resets to Windows' default network configuration - whatever custom settings were in place aren't recorded anywhere to restore.",
        PreviewCommand = null, // no safe read-only equivalent exists for a stack reset
        Execute = _ => RunToolAsync("netsh.exe", "int ip reset", timeoutMs: 30_000),
    };

    /// <summary>Ties directly to the built-in pack's CPU-hot rules: a power plan stuck with a high
    /// minimum processor state keeps the CPU (and fans) from ever idling down, independent of
    /// actual load - see TroubleshootService.CheckMinProcessorStateAsync, which reads the same
    /// powercfg query this reuses for its "before" reading.</summary>
    public static RemediationAction LowerMinProcessorState() => new()
    {
        Id = "power.lower-min-proc-state",
        Title = "Lower minimum processor state (reduce heat)",
        PlainEnglishDescription = "Lowers the active power plan's minimum CPU state to 5% on AC power, so the CPU can idle down further instead of holding a needlessly high clock/voltage floor - a common contributor to running hot without real load behind it.",
        Command = "powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 5 (then: powercfg /setactive SCHEME_CURRENT to apply)",
        RiskLevel = RemediationRiskLevel.Low,
        RequiresReboot = false,
        IsUndoable = true,
        JournalKind = ChangeKind.PowerSettingChange,
        PreviewCommand = "powercfg /query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN",
        ExecutePreview = async _ =>
        {
            var (output, exitCode) = await TroubleshootService.RunCapturedAsync(
                "powercfg.exe", "/query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN", timeoutMs: 10_000);
            return exitCode is not null ? RemediationRunResult.Ok(output) : RemediationRunResult.Fail("powercfg /query timed out.");
        },
        Execute = async _ =>
        {
            string? before = await ReadMinProcessorStateAcPercentAsync();

            var (setOutput, setExit) = await TroubleshootService.RunCapturedAsync(
                "powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 5", timeoutMs: 10_000);
            if (setExit != 0)
                return RemediationRunResult.Fail(string.IsNullOrWhiteSpace(setOutput) ? "powercfg /setacvalueindex failed." : setOutput.Trim());

            var (activateOutput, activateExit) = await TroubleshootService.RunCapturedAsync(
                "powercfg.exe", "/setactive SCHEME_CURRENT", timeoutMs: 10_000);
            if (activateExit != 0)
                return RemediationRunResult.Fail(string.IsNullOrWhiteSpace(activateOutput) ? "powercfg /setactive failed." : activateOutput.Trim());

            return RemediationRunResult.Ok("Minimum processor state set to 5% on AC power.",
                before: before is null ? null : $"{before}%", after: "5%");
        },
    };

    private static async Task<string?> ReadMinProcessorStateAcPercentAsync()
    {
        try
        {
            var (output, exitCode) = await TroubleshootService.RunCapturedAsync(
                "powercfg.exe", "/query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN", timeoutMs: 10_000);
            if (exitCode is null) return null;
            var m = Regex.Match(output, @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)");
            return m.Success ? Convert.ToInt32(m.Groups[1].Value, 16).ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    // ----- parameterized actions (need a runtime target) ---------------------------------------

    /// <summary>#967: "chkdsk C: /scan (parameterized by drive)" - an online, read-only scan
    /// (no dismount, no reboot on modern Windows), so unlike a repair pass this needs no
    /// PreviewCommand of its own; it already IS the safe/informational variant.</summary>
    public static RemediationAction ChkdskScan(string driveLetter)
    {
        string drive = driveLetter.TrimEnd(':').Trim().ToUpperInvariant() + ":";
        return new RemediationAction
        {
            Id = "storage.chkdsk-scan",
            Title = $"Scan {drive} for file system errors",
            PlainEnglishDescription = $"Runs an online, read-only file system scan of {drive} (no dismount, no reboot needed) and reports what it finds. Doesn't fix anything by itself.",
            Command = $"chkdsk {drive} /scan",
            RiskLevel = RemediationRiskLevel.Low,
            RequiresReboot = false,
            IsUndoable = false,
            NotUndoableReason = "A read-only scan doesn't change anything - there's nothing to undo.",
            PreviewCommand = null,
            Execute = _ => RunToolAsync("chkdsk.exe", $"{drive} /scan", timeoutMs: 300_000),
        };
    }

    /// <summary>Reuses ServiceControlService.Restart - the same call the Services tab's own
    /// Restart button already makes.</summary>
    public static RemediationAction RestartService(string serviceName, string displayName) => new()
    {
        Id = "services.restart-failed",
        Title = $"Restart {displayName}",
        PlainEnglishDescription = $"Stops and restarts the \"{displayName}\" service - the same action available from the Services tab.",
        Command = $"sc.exe stop \"{serviceName}\" && sc.exe start \"{serviceName}\"",
        RiskLevel = RemediationRiskLevel.Medium,
        RequiresReboot = false,
        IsUndoable = false,
        NotUndoableReason = "A restart can't be meaningfully undone - the service is simply left running.",
        JournalKind = ChangeKind.ServiceStateChange,
        ServiceName = serviceName,
        PreviewCommand = null,
        Execute = _ => Task.Run(() =>
        {
            var (success, error) = ServiceControlService.Restart(serviceName);
            return success
                ? RemediationRunResult.Ok($"{displayName} restarted.")
                : RemediationRunResult.Fail(error ?? "Unknown error.");
        }),
    };

    /// <summary>Reuses StartupManagerService.SetEnabled - the same call the Startup tab's own
    /// enable/disable toggle already makes. #971: RegistryKeyToBackup is set here (unlike the plain
    /// Startup-tab toggle, which doesn't back anything up) since this app's own C# code performs
    /// this exact registry write and therefore knows precisely which key to export first.</summary>
    public static RemediationAction DisableStartupItem(StartupItem item)
    {
        var (hiveText, subPath, valueName) = item.Source switch
        {
            StartupSource.RegistryRunHkcu => ("HKCU", StartupManagerService.ApprovedRunKeyPath, item.Name),
            StartupSource.RegistryRunHklm => ("HKLM", StartupManagerService.ApprovedRunKeyPath, item.Name),
            StartupSource.RegistryRunHklmWow6432 => ("HKLM", StartupManagerService.ApprovedRunKeyPath, item.Name),
            StartupSource.StartupFolderUser => ("HKCU", StartupManagerService.ApprovedFolderKeyPath, Path.GetFileName(item.Command)),
            StartupSource.StartupFolderAllUsers => ("HKCU", StartupManagerService.ApprovedFolderKeyPath, Path.GetFileName(item.Command)),
            _ => ("HKCU", StartupManagerService.ApprovedRunKeyPath, item.Name),
        };
        string fullKey = $"{hiveText}\\{subPath}";

        return new RemediationAction
        {
            Id = "startup.disable-item",
            Title = $"Disable \"{item.Name}\" at startup",
            PlainEnglishDescription = $"Stops \"{item.Name}\" from launching automatically at sign-in. Doesn't uninstall it or delete anything - the same flag Explorer's own Startup Apps list flips.",
            Command = $"reg add \"{fullKey}\" /v \"{valueName}\" /t REG_BINARY /d 030000000000000000000000 /f",
            RiskLevel = RemediationRiskLevel.Low,
            RequiresReboot = false,
            IsUndoable = true,
            JournalKind = ChangeKind.StartupToggle,
            StartupItemName = item.Name,
            StartupItemCommand = item.Command,
            StartupItemSource = item.Source.ToString(),
            PreviewCommand = null,
            RegistryKeyToBackup = fullKey,
            Execute = _ =>
            {
                var (success, error) = StartupManagerService.SetEnabled(item, false);
                return Task.FromResult(success
                    ? RemediationRunResult.Ok($"\"{item.Name}\" disabled at startup.", before: "Enabled", after: "Disabled")
                    : RemediationRunResult.Fail(error ?? "Unknown error."));
            },
        };
    }

    /// <summary>Reuses ProcessControlService.SetPriority - the same call the Processes tab's own
    /// priority combo box already makes.</summary>
    public static RemediationAction LowerProcessPriority(int pid, string processName, ProcessPriorityClass targetPriority) => new()
    {
        Id = "process.lower-priority",
        Title = $"Lower priority of {processName} (PID {pid})",
        PlainEnglishDescription = $"Sets {processName}'s scheduling priority to {targetPriority} so it competes less aggressively for CPU time. Doesn't end or restart the process.",
        Command = $"powershell -Command \"(Get-Process -Id {pid}).PriorityClass = '{targetPriority}'\"",
        RiskLevel = RemediationRiskLevel.Low,
        RequiresReboot = false,
        IsUndoable = true,
        JournalKind = ChangeKind.ProcessPriorityChange,
        Pid = pid,
        ProcessName = processName,
        PreviewCommand = null,
        Execute = _ =>
        {
            string? before = null;
            try { before = Process.GetProcessById(pid).PriorityClass.ToString(); }
            catch { /* already exited - Execute below will report that itself */ }

            var (success, error) = ProcessControlService.SetPriority(pid, targetPriority);
            return Task.FromResult(success
                ? RemediationRunResult.Ok($"Priority set to {targetPriority}.", before: before, after: targetPriority.ToString())
                : RemediationRunResult.Fail(error ?? "Unknown error."));
        },
    };

    // ----- resolving a fired finding's ActionIds into concrete actions (#967) ------------------

    /// <summary>Turns `issue.ActionIds` into ready-to-review RemediationAction instances. An id
    /// whose required context can't be found this pass (no drive parsed out of the message, no
    /// currently-failed service) is simply omitted rather than shown broken - "Fix this" only
    /// appears at all when at least one action actually resolved (see HealthIssue.HasFixAction on
    /// the finding, checked again here defensively).</summary>
    public static List<RemediationAction> Resolve(HealthIssue issue, ServicesViewModel services)
    {
        var result = new List<RemediationAction>();
        if (issue.ActionIds is not { Count: > 0 } ids) return result;

        foreach (var id in ids)
        {
            switch (id)
            {
                case "system.sfc-scannow":
                    result.Add(SfcScan());
                    break;
                case "system.dism-restorehealth":
                    result.Add(DismRestoreHealth());
                    break;
                case "network.reset-tcpip":
                    result.Add(NetshResetTcpIp());
                    break;
                case "power.lower-min-proc-state":
                    result.Add(LowerMinProcessorState());
                    break;
                case "storage.chkdsk-scan":
                    var drive = ExtractDriveFromMessage(issue.Message);
                    if (drive is not null) result.Add(ChkdskScan(drive));
                    break;
                case "services.restart-failed":
                    var failed = services.Services.FirstOrDefault(s => s.HasFailedToStart);
                    if (failed is not null) result.Add(RestartService(failed.ServiceName, failed.DisplayName));
                    break;
                // "startup.disable-item" and "process.lower-priority" aren't wired to any built-in
                // rule (RulesEngineService's metric bag carries no single-item/single-process
                // identity to target - see BuildMetricBag), but stay available here as real,
                // fully-wired actions a custom rule (or a later chunk's UI) can reference by id.
            }
        }
        return result;
    }

    /// <summary>The dirty-bit/volume-full rules' Body templates resolve to e.g. "C: needs a chkdsk
    /// pass..." - the drive letter isn't part of the fired condition's own comparison (only its
    /// Body template), so it isn't captured in ConditionReadings/Evidence; parsing the already-
    /// resolved message is the simplest honest way to recover it without changing the rules
    /// engine's evidence-capture shape for one caller.</summary>
    private static string? ExtractDriveFromMessage(string message)
    {
        var m = Regex.Match(message, @"^\s*([A-Za-z]):");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static async Task<RemediationRunResult> RunToolAsync(string exe, string args, int timeoutMs)
    {
        var (output, exitCode) = await TroubleshootService.RunCapturedAsync(exe, args, timeoutMs);
        return exitCode is not null
            ? RemediationRunResult.Ok(output)
            : RemediationRunResult.Fail(string.IsNullOrWhiteSpace(output) ? "Command timed out or failed to start." : output);
    }
}
