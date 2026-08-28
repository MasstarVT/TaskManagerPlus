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
    /// <summary>suggestions.md #1000: the subset of this catalog that needs no live target (drive
    /// letter, service name, startup item, process id) to describe - what the Ctrl+K command
    /// palette's "remediation action" search category lists. The parameterized factories below
    /// (ChkdskScan/RestartService/DisableStartupItem/LowerProcessPriority) only make sense resolved
    /// against a real finding (see Resolve), so aren't included here.</summary>
    public static List<RemediationAction> SystemWideCatalog() => new()
    {
        SfcScan(),
        DismRestoreHealth(),
        NetshResetTcpIp(),
        LowerMinProcessorState(),
    };

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
        // #974: advisory only (see PreconditionKind.RequiresSystemProtectionOn's remarks) - Medium/
        // High-risk actions already let the user Skip the restore-point prompt and run anyway.
        Preconditions = { new RemediationPrecondition { Kind = PreconditionKind.RequiresSystemProtectionOn, Blocking = false } },
        PreviewCommand = "sfc /verifyonly",
        ExecutePreview = _ => RunToolAsync("sfc.exe", "/verifyonly", timeoutMs: 1_800_000),
        Execute = _ => RunToolAsync("sfc.exe", "/scannow", timeoutMs: 1_800_000),
        // #977: sfc's own output isn't cleanly percentage-parseable - streamed live, but with no
        // ParseProgressPercent, so the review dialog shows an honest indeterminate progress state.
        ExecutePreviewStreaming = (ct, onLine) => RunToolStreamingAsync("sfc.exe", "/verifyonly", onLine, ct, timeoutMs: 1_800_000),
        ExecuteStreaming = (ct, onLine) => RunToolStreamingAsync("sfc.exe", "/scannow", onLine, ct, timeoutMs: 1_800_000),
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
        // #974: DISM's component-store repair is documented to be unreliable while a prior
        // update's reboot is still outstanding - a real, blocking requirement (unlike the advisory
        // System Protection check below).
        Preconditions =
        {
            new RemediationPrecondition { Kind = PreconditionKind.RequiresNoRebootPending, Blocking = true },
            new RemediationPrecondition { Kind = PreconditionKind.RequiresSystemProtectionOn, Blocking = false },
        },
        PreviewCommand = "DISM /Online /Cleanup-Image /ScanHealth",
        ExecutePreview = _ => RunToolAsync("DISM.exe", "/Online /Cleanup-Image /ScanHealth", timeoutMs: 900_000),
        Execute = _ => RunToolAsync("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", timeoutMs: 1_800_000),
        // #977: DISM's own progress readout ("[ XX.X% ]") is cleanly parseable - a real progress bar.
        ExecutePreviewStreaming = (ct, onLine) => RunToolStreamingAsync("DISM.exe", "/Online /Cleanup-Image /ScanHealth", onLine, ct, timeoutMs: 900_000),
        ExecuteStreaming = (ct, onLine) => RunToolStreamingAsync("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", onLine, ct, timeoutMs: 1_800_000),
        ParseProgressPercent = ParseDismProgressPercent,
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
        Preconditions = { new RemediationPrecondition { Kind = PreconditionKind.RequiresSystemProtectionOn, Blocking = false } },
        PreviewCommand = null, // no safe read-only equivalent exists for a stack reset
        Execute = _ => RunToolAsync("netsh.exe", "int ip reset", timeoutMs: 30_000),
    };

    /// <summary>#977: DISM's "[ XX.X% ]" progress readout.</summary>
    private static double? ParseDismProgressPercent(string line)
    {
        var m = Regex.Match(line, @"\[\s*=*\s*(\d+(?:\.\d+)?)%\s*=*\s*\]");
        return m.Success && double.TryParse(m.Groups[1].Value, out var pct) ? pct : null;
    }

    /// <summary>#977: chkdsk's "NN percent complete" progress lines.</summary>
    private static double? ParseChkdskProgressPercent(string line)
    {
        var m = Regex.Match(line, @"(\d{1,3})\s*percent complete", RegexOptions.IgnoreCase);
        return m.Success && double.TryParse(m.Groups[1].Value, out var pct) ? pct : null;
    }

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
        string drive = NormalizeDrive(driveLetter);
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
            // #974: this app offers the guided chkdsk fix only for NTFS volumes, so the scan
            // variant is held to the same requirement (a scan against a volume this app would then
            // refuse to offer /f for isn't a useful first step here).
            Preconditions = { new RemediationPrecondition { Kind = PreconditionKind.RequiresNtfsVolume, Parameter = drive, Blocking = true } },
            PreviewCommand = null,
            Execute = _ => RunToolAsync("chkdsk.exe", $"{drive} /scan", timeoutMs: 300_000),
            ExecuteStreaming = (ct, onLine) => RunToolStreamingAsync("chkdsk.exe", $"{drive} /scan", onLine, ct, timeoutMs: 300_000),
            ParseProgressPercent = ParseChkdskProgressPercent,
        };
    }

    /// <summary>#979: the "/f" repair variant - unlike the read-only scan above, this needs the
    /// volume offline whenever it's the system/boot drive (chkdsk schedules itself for the next
    /// boot in that case, same as the classic Task Manager/Disk Management flow) - the review
    /// dialog's "Queue for next boot" option exists specifically for this action.</summary>
    public static RemediationAction ChkdskFix(string driveLetter)
    {
        string drive = NormalizeDrive(driveLetter);
        return new RemediationAction
        {
            Id = "storage.chkdsk-fix",
            Title = $"Fix file system errors on {drive}",
            PlainEnglishDescription = $"Runs chkdsk {drive} /f, which actually repairs file system errors rather than just reporting them. If {drive} is in use (the system drive almost always is), Windows can't lock it live - queue this for next boot instead of running it now.",
            Command = $"chkdsk {drive} /f",
            RiskLevel = RemediationRiskLevel.Medium,
            RequiresReboot = false,
            IsUndoable = false,
            NotUndoableReason = "A file system repair pass doesn't record what it changed - there's nothing to reverse.",
            Preconditions = { new RemediationPrecondition { Kind = PreconditionKind.RequiresNtfsVolume, Parameter = drive, Blocking = true } },
            PreviewCommand = null,
            SupportsDeferredQueue = true,
            Execute = _ => RunToolAsync("chkdsk.exe", $"{drive} /f", timeoutMs: 300_000),
            ExecuteStreaming = (ct, onLine) => RunToolStreamingAsync("chkdsk.exe", $"{drive} /f", onLine, ct, timeoutMs: 300_000),
            ParseProgressPercent = ParseChkdskProgressPercent,
        };
    }

    private static string NormalizeDrive(string driveLetter) => driveLetter.TrimEnd(':').Trim().ToUpperInvariant() + ":";

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
        // #974: the service that fired this finding may no longer exist by the time the review
        // dialog is opened (uninstalled, or the finding is from a stale/reopened past run).
        Preconditions =
        {
            new RemediationPrecondition { Kind = PreconditionKind.RequiresServicePresent, Parameter = serviceName, Blocking = true },
            new RemediationPrecondition { Kind = PreconditionKind.RequiresSystemProtectionOn, Blocking = false },
        },
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
                    if (drive is not null)
                    {
                        result.Add(ChkdskScan(drive));
                        // #979: the /f "actually fix it" variant alongside the read-only scan -
                        // both selectable from the same review dialog's Action combo box.
                        result.Add(ChkdskFix(drive));
                    }
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

    /// <summary>
    /// #977: the streaming twin of RunToolAsync/TroubleshootService.RunCapturedAsync - reports each
    /// stdout/stderr line to `onLine` as it arrives (Process.OutputDataReceived/
    /// ErrorDataReceived + BeginOutputReadLine, the standard non-deadlocking async-read pattern)
    /// instead of only returning the full text after the process exits. `ct` is the *user's*
    /// cancel token (RemediationReviewViewModel's Cancel button) - cancelling it kills the process
    /// and returns RemediationRunResult.Cancel rather than .Fail, so the review dialog and the
    /// journal entry it writes can say "cancelled" honestly. A separate internal timeout applies
    /// regardless of user cancellation, same ceiling every other catalog action already uses.
    /// </summary>
    private static async Task<RemediationRunResult> RunToolStreamingAsync(string exe, string args, Action<string> onLine, CancellationToken ct, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var allOutput = new System.Text.StringBuilder();
        void HandleLine(string? line)
        {
            if (line is null) return;
            lock (allOutput) allOutput.AppendLine(line);
            try { onLine(line); } catch { /* a misbehaving UI-side handler must never take the process down */ }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => HandleLine(e.Data);
        process.ErrorDataReceived += (_, e) => HandleLine(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return RemediationRunResult.Fail($"Couldn't start {exe}: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            string soFar;
            lock (allOutput) soFar = allOutput.ToString();
            return ct.IsCancellationRequested
                ? RemediationRunResult.Cancel(soFar)
                : RemediationRunResult.Fail(soFar + "\n(command timed out)");
        }

        string finalOutput;
        lock (allOutput) finalOutput = allOutput.ToString();
        return process.ExitCode == 0 ? RemediationRunResult.Ok(finalOutput) : RemediationRunResult.Fail(finalOutput);
    }
}
