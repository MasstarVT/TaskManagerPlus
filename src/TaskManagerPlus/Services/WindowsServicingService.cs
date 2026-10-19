using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #770/#773: the two heavier, explicitly on-demand actions in the Windows Health tab's servicing
/// section - a full `dism /online /get-packages` sweep (can take several seconds) and a feature-
/// update failure-log parse (potentially large log files under $WINDOWS.~BT/Panther). Sibling to
/// WindowsUpdateHistoryService (#769/#771/#772's shared error catalog) rather than merged into it,
/// since these two are heavier, separately-triggered actions rather than part of the same "load the
/// history" query.
/// </summary>
public static class WindowsServicingService
{
    /// <summary>#770: `dism /online /get-packages /format:table` - every installed servicing
    /// package with its current state (Installed, Install Pending, Uninstall Pending, Superseded,
    /// Failed, ...). DISM itself can take a while to enumerate a large package store, so this is
    /// gated behind its own explicit button rather than loaded with the rest of the tab. Error is
    /// set (and the list is empty) when DISM failed, timed out or listed nothing recognizable - a
    /// running Windows always has packages, so an empty list is never a real answer.</summary>
    public static async Task<(List<ServicingPackageInfo> Packages, string? Error)> ListPackagesAsync()
    {
        var result = new List<ServicingPackageInfo>();
        try
        {
            var (output, exitCode) = await RunCapturedAsync("dism.exe", "/online /get-packages /format:table", 120000);
            if (exitCode != 0)
            {
                string? errorLine = output.Split('\n').Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith("Error", StringComparison.OrdinalIgnoreCase));
                return (result, exitCode == -1
                    ? "DISM timed out listing the servicing packages (it may be busy with another servicing operation)."
                    : $"DISM couldn't list the servicing packages (exit code {exitCode}){(errorLine is null ? "." : $": {errorLine}")}");
            }
            foreach (var rawLine in output.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0 || !line.Contains('|')) continue;
                if (line.StartsWith("---", StringComparison.Ordinal)) continue;
                if (line.StartsWith("Package Identity", StringComparison.OrdinalIgnoreCase)) continue; // header row

                // Columns are Package Identity | State | Release Type | Install Time - take the
                // first two by position (also correct for a two-column table).
                var cols = line.Split('|');
                string identity = cols[0].Trim();
                string state = cols[1].Trim();
                if (identity.Length == 0 || state.Length == 0) continue;

                result.Add(new ServicingPackageInfo { PackageIdentity = identity, State = state });
            }
        }
        catch (Exception ex)
        {
            return (new List<ServicingPackageInfo>(), $"Couldn't run DISM: {ex.Message}");
        }
        return result.Count == 0
            ? (result, "DISM finished but listed no packages in a format this app recognizes.")
            : (result, null);
    }

    #region #773 - Feature update failure analysis

    private static readonly string PantherBtPath = Path.Combine(@"C:\$WINDOWS.~BT\Sources\Panther");
    private static readonly string PantherCommittedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Panther");

    private static readonly Regex HostLineRegex = new(@"^\s*Host:\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex RollbackLineRegex = new(@"^.*Rollback.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Panther lines are "date, Level  Component  message", so the level ("Error") comes before
    // "MOUPG"; either order is accepted in case a message itself mentions an error.
    private static readonly Regex MoupgErrorRegex = new(@"^(?=.*\bMOUPG\b)(?=.*\bError\b).*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>#773: looks for setuperr.log/setupact.log under C:\$WINDOWS.~BT\Sources\Panther
    /// (an in-progress or just-failed upgrade that hasn't been cleaned up yet) first, then
    /// C:\Windows\Panther (an upgrade that got far enough to commit some files before failing).
    /// LogsFound is false, and every other field null/empty, when neither location has anything -
    /// the common case on a machine that's never attempted a feature update, per this app's
    /// "degrade to Unknown/hidden, never fabricate" convention.</summary>
    public static FeatureUpdateFailureInfo AnalyzeFeatureUpdateFailure()
    {
        string? logPath = FindLogFile(PantherBtPath, "setuperr.log")
            ?? FindLogFile(PantherBtPath, "setupact.log")
            ?? FindLogFile(PantherCommittedPath, "setuperr.log")
            ?? FindLogFile(PantherCommittedPath, "setupact.log");

        if (logPath is null)
        {
            return new FeatureUpdateFailureInfo { LogsFound = false };
        }

        string text;
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return new FeatureUpdateFailureInfo
            {
                LogsFound = true,
                SourceLogPath = logPath,
                FailingPhase = $"(couldn't read log: {ex.Message})",
            };
        }

        var hostMatches = HostLineRegex.Matches(text);
        string? failingPhase = hostMatches.Count > 0 ? hostMatches[^1].Groups[1].Value.Trim() : null;

        var rollbackMatches = RollbackLineRegex.Matches(text);
        string? rollbackReason = rollbackMatches.Count > 0 ? rollbackMatches[^1].Value.Trim() : null;

        var moupgLines = MoupgErrorRegex.Matches(text)
            .Select(m => m.Value.Trim())
            .Where(l => l.Length > 0)
            .Distinct()
            .Take(25)
            .ToList();

        return new FeatureUpdateFailureInfo
        {
            LogsFound = true,
            SourceLogPath = logPath,
            FailingPhase = failingPhase,
            RollbackReason = rollbackReason,
            MoupgErrorLines = moupgLines,
            SetupDiagAvailable = FindSetupDiagPath() is not null,
        };
    }

    private static string? FindLogFile(string dir, string fileName)
    {
        try
        {
            string path = Path.Combine(dir, fileName);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    // SetupDiag isn't installed by default - it's a standalone download from Microsoft, so this
    // only checks the couple of places someone would plausibly have dropped it rather than
    // assuming any one fixed path.
    private static string[] SetupDiagCandidates() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SetupDiag", "SetupDiag.exe"),
        Path.Combine(PantherBtPath, "SetupDiag.exe"),
        Path.Combine(AppContext.BaseDirectory, "SetupDiag.exe"),
        @"C:\SetupDiag\SetupDiag.exe",
    };

    private static string? FindSetupDiagPath()
    {
        foreach (var candidate in SetupDiagCandidates())
        {
            try { if (File.Exists(candidate)) return candidate; }
            catch { /* ignore this candidate, try the next */ }
        }
        return null;
    }

    /// <summary>This app runs elevated, so a SetupDiag.exe it launches must be one a standard user
    /// couldn't have planted or swapped: C:\SetupDiag and C:\$WINDOWS.~BT can be created by any
    /// user (C:\ grants "create folders"). Requires an admin-only folder chain AND an embedded
    /// Microsoft signature; a signature alone would still race a swap in a user-writable folder.</summary>
    private static string? SetupDiagTrustProblem(string path)
    {
        string? writable = UnattendedScanService.FindNonAdminWriteAccess(path);
        if (writable is not null) return writable;
        var sig = SignatureCheckService.GetResult(path);
        bool microsoftSigned = sig.Verification == SignatureVerification.SignedEmbedded &&
            (sig.SubjectCn?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ?? false);
        return microsoftSigned ? null : $"{path} isn't Microsoft-signed";
    }

    /// <summary>#773: runs SetupDiag.exe (when present - see FindSetupDiagPath) against the newest
    /// available upgrade logs and returns its own summarized verdict line, instead of this app's
    /// own regex-based log parse. Never downloads or installs SetupDiag itself - this app takes no
    /// network dependency for this feature, matching CLAUDE.md's "prefer a known Windows tool"
    /// convention without silently fetching a binary from the internet.</summary>
    public static async Task<(bool Success, string? Verdict, string? Error)> RunSetupDiagAsync()
    {
        // ACL walk + WinVerifyTrust per candidate - off the caller's (UI) thread.
        var (exePath, rejection) = await Task.Run(() =>
        {
            string? rejected = null;
            foreach (var candidate in SetupDiagCandidates())
            {
                try { if (!File.Exists(candidate)) continue; }
                catch { continue; }
                string? problem = SetupDiagTrustProblem(candidate);
                if (problem is null) return ((string?)candidate, (string?)null);
                rejected ??= problem;
            }
            return ((string?)null, rejected);
        });
        if (exePath is null)
        {
            return (false, null, rejection is null
                ? "SetupDiag.exe wasn't found on this machine."
                : $"Found SetupDiag.exe but won't run it elevated: {rejection}. Put Microsoft's SetupDiag.exe in an admin-only folder such as {Path.GetDirectoryName(SetupDiagCandidates()[0])} and try again.");
        }

        // The elevated SetupDiag writes its results file, which is then read back - so it goes in a
        // fresh admin-only folder, never a predictable path under the user's %TEMP% that a junction
        // could redirect. No such folder, no run.
        var outputDir = AdminDataPaths.CreatePrivateTempDirectory("SetupDiag", out string? dirError);
        if (outputDir is null)
            return (false, null, $"Won't run SetupDiag: {dirError}");

        try
        {
            string resultsPath = Path.Combine(outputDir.FullName, "SetupDiagResults.log");

            var (output, exitCode) = await RunCapturedAsync(exePath, $"/Output:\"{resultsPath}\" /Format:Text", 120000);

            string verdict;
            if (File.Exists(resultsPath))
            {
                verdict = (await File.ReadAllTextAsync(resultsPath)).Trim();
            }
            else
            {
                verdict = output.Trim();
            }

            if (verdict.Length == 0) verdict = "SetupDiag ran but produced no readable output.";
            return (exitCode == 0, Truncate(verdict, 2000), null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
        finally
        {
            AdminDataPaths.TryDelete(outputDir);
        }
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";

    #endregion

    #region #778 - Guided "reset Windows Update components"

    private static readonly string[] ResetComponentsServices = { "wuauserv", "CryptSvc", "BITS", "msiserver" };

    /// <summary>
    /// #778: the documented Windows Update component-reset repair sequence - stop the four core
    /// services, rename SoftwareDistribution and catroot2 out of the way so Windows recreates them
    /// from scratch, then restart the services. Run only as one explicit, confirmed action (the
    /// confirmation dialog with the exact steps lives in WindowsHealthViewModel, per CLAUDE.md's
    /// "guided, never automatic" rule for this exact feature) - never on a schedule, never silent.
    /// Returns a step-by-step log (including the undo instructions for each rename) rather than a
    /// bare success bool, so the user can see exactly what happened and how to reverse it. Blocking
    /// (ServiceControlService.Stop/Start each wait synchronously for the service to settle) - the
    /// caller runs this via Task.Run, the same "synchronous service, backgrounded by the ViewModel"
    /// shape WindowsUpdatePolicyService.ReadRebootPendingDetail already uses. Success means both
    /// renames went through (or had nothing to rename) and every service came back up.
    /// </summary>
    public static (List<string> Log, bool Success) ResetWindowsUpdateComponents()
    {
        var log = new List<string>();
        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string softwareDistribution = Path.Combine(windowsDir, "SoftwareDistribution");
        string catroot2 = Path.Combine(windowsDir, "System32", "catroot2");

        log.Add($"[{DateTime.Now:T}] Starting guided Windows Update component reset.");

        foreach (var svc in ResetComponentsServices)
            log.Add(StopLogged(svc, out _));

        var (sdLine, sdOk) = RenameToBak(softwareDistribution);
        var (crLine, crOk) = RenameToBak(catroot2);
        log.Add(sdLine);
        log.Add(crLine);

        bool allStarted = true;
        foreach (var svc in ResetComponentsServices)
        {
            log.Add(StartLogged(svc, out bool started));
            allStarted &= started;
        }

        log.Add($"[{DateTime.Now:T}] Done. Windows recreates SoftwareDistribution and catroot2 automatically the next time it checks for updates.");
        return (log, sdOk && crOk && allStarted);
    }

    /// <summary>ServiceController.Stop/Start throw on a service that is already in the target state
    /// (msiserver is normally stopped), so that case is logged as done rather than as a failure.</summary>
    private static string StopLogged(string serviceName, out bool ok)
    {
        if (ReadServiceStatus(serviceName) == System.ServiceProcess.ServiceControllerStatus.Stopped)
        {
            ok = true;
            return $"[{DateTime.Now:T}] {serviceName} was already stopped.";
        }
        var (success, error) = ServiceControlService.Stop(serviceName);
        ok = success;
        return success
            ? $"[{DateTime.Now:T}] Stopped {serviceName}."
            : $"[{DateTime.Now:T}] Couldn't stop {serviceName}: {error} (continuing anyway).";
    }

    private static string StartLogged(string serviceName, out bool ok)
    {
        if (ReadServiceStatus(serviceName) == System.ServiceProcess.ServiceControllerStatus.Running)
        {
            ok = true;
            return $"[{DateTime.Now:T}] {serviceName} is already running.";
        }
        var (success, error) = ServiceControlService.Start(serviceName);
        ok = success;
        return success
            ? $"[{DateTime.Now:T}] Started {serviceName}."
            : $"[{DateTime.Now:T}] Couldn't start {serviceName}: {error}.";
    }

    private static System.ServiceProcess.ServiceControllerStatus? ReadServiceStatus(string serviceName)
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(serviceName);
            return sc.Status;
        }
        catch
        {
            return null; // not installed / access denied - let Stop/Start report the real error
        }
    }

    /// <summary>Renames `path` to `path.bak` (or `path.bak.<timestamp>` if a `.bak` from a previous
    /// reset already exists, so a second reset never clobbers the first one's undo point), logging
    /// the exact undo step alongside the result - the log line itself carries "how to undo this",
    /// not a separate lookup the user has to remember. Ok is true when there was nothing to rename.</summary>
    private static (string Line, bool Ok) RenameToBak(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return ($"[{DateTime.Now:T}] {path} doesn't exist - nothing to rename, skipping.", true);

            string bakPath = path + ".bak";
            if (Directory.Exists(bakPath))
                bakPath = $"{path}.bak.{DateTime.Now:yyyyMMddHHmmss}";

            Directory.Move(path, bakPath);
            return ($"[{DateTime.Now:T}] Renamed \"{path}\" to \"{bakPath}\". To undo: stop the services above, then rename \"{bakPath}\" back to \"{path}\".", true);
        }
        catch (Exception ex)
        {
            return ($"[{DateTime.Now:T}] Couldn't rename \"{path}\": {ex.Message}", false);
        }
    }

    #endregion

    #region #779 - Update cache reclaim

    private static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    // DoSvc keeps its cache under its own service profile, not SoftwareDistribution\DeliveryOptimization
    // (that folder doesn't exist on current Windows), so its size and its clear both go through
    // the DeliveryOptimization module, which asks DoSvc itself.
    private const string DeliveryOptimizationSizeArgs = "-NoProfile -NonInteractive -Command \"(Get-DeliveryOptimizationPerfSnap -ErrorAction Stop).CacheSizeBytes\"";
    private const string DeliveryOptimizationClearArgs = "-NoProfile -NonInteractive -Command \"Delete-DeliveryOptimizationCache -Force -ErrorAction Stop\"";

    /// <summary>The literal command the Clear action runs for the Delivery Optimization cache.</summary>
    public const string DeliveryOptimizationClearCommandLine = "powershell.exe " + DeliveryOptimizationClearArgs;

    private const string DeliveryOptimizationCacheLabel = "Managed by the Delivery Optimization service (DoSvc) - size as DoSvc reports it";

    /// <summary>#779: measures the two update-related caches on demand (a recursive directory walk
    /// plus one PowerShell call, per CLAUDE.md's "gated behind an explicit button" rule for anything
    /// heavier than a trivial read) - the WU download cache (wuauserv) and the Delivery Optimization
    /// cache (DoSvc), routinely the largest single reclaimable chunk of space on a machine that's
    /// been running Windows Update for a while. The DO size is null (Unknown) when DoSvc couldn't
    /// report it.</summary>
    public static UpdateCacheInfo ReadUpdateCacheSizes()
    {
        string downloadPath = Path.Combine(WindowsDirectory, "SoftwareDistribution", "Download");
        var (doSize, doError) = ReadDeliveryOptimizationCacheSize();
        return new UpdateCacheInfo
        {
            DownloadCachePath = downloadPath,
            DownloadCacheSizeBytes = ComputeDirectorySize(downloadPath),
            DeliveryOptimizationCachePath = DeliveryOptimizationCacheLabel,
            DeliveryOptimizationCacheSizeBytes = doSize,
            DeliveryOptimizationError = doError,
        };
    }

    private static (long? Bytes, string? Error) ReadDeliveryOptimizationCacheSize()
    {
        try
        {
            var (output, exitCode) = ToolRunner.RunCaptured("powershell.exe", DeliveryOptimizationSizeArgs, TimeSpan.FromSeconds(30));
            if (exitCode is null) return (null, "Get-DeliveryOptimizationPerfSnap timed out.");
            string text = output.Trim();
            if (exitCode == 0 && long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long bytes) && bytes >= 0)
                return (bytes, null);
            string firstLine = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            return (null, $"Get-DeliveryOptimizationPerfSnap didn't report a cache size (exit code {exitCode}){(firstLine.Length > 0 ? ": " + Truncate(firstLine, 200) : ".")}");
        }
        catch (Exception ex)
        {
            return (null, $"Couldn't run Get-DeliveryOptimizationPerfSnap: {ex.Message}");
        }
    }

    private static long ComputeDirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* file removed/locked mid-walk - skip it, same as every other best-effort file read in this app */ }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>#779: the download cache: stops wuauserv, clears every item directly under the
    /// folder, then restarts it. The Delivery Optimization cache: Delete-DeliveryOptimizationCache,
    /// which DoSvc carries out itself (so DoSvc stays running). Confirmed by the caller before this
    /// runs (WindowsHealthViewModel). Returns a step-by-step log, same shape as #778's reset action,
    /// plus whether everything was cleared and the services came back up.</summary>
    public static async Task<(List<string> Log, bool Success)> ClearUpdateCacheAsync(bool clearDownloadCache, bool clearDeliveryOptimizationCache)
    {
        return await Task.Run(() =>
        {
            var log = new List<string>();
            bool ok = true;
            if (clearDownloadCache)
                ok &= ClearCacheFolder("wuauserv", Path.Combine(WindowsDirectory, "SoftwareDistribution", "Download"), log);
            if (clearDeliveryOptimizationCache)
                ok &= ClearDeliveryOptimizationCache(log);
            return (log, ok);
        });
    }

    private static bool ClearDeliveryOptimizationCache(List<string> log)
    {
        try
        {
            var (output, exitCode) = ToolRunner.RunCaptured("powershell.exe", DeliveryOptimizationClearArgs, TimeSpan.FromMinutes(2));
            if (exitCode == 0)
            {
                log.Add($"[{DateTime.Now:T}] Delivery Optimization cache cleared (Delete-DeliveryOptimizationCache).");
                return true;
            }
            log.Add(exitCode is null
                ? $"[{DateTime.Now:T}] Delete-DeliveryOptimizationCache timed out."
                : $"[{DateTime.Now:T}] Delete-DeliveryOptimizationCache failed (exit code {exitCode}): {Truncate(output.Trim(), 300)}");
            return false;
        }
        catch (Exception ex)
        {
            log.Add($"[{DateTime.Now:T}] Couldn't run Delete-DeliveryOptimizationCache: {ex.Message}");
            return false;
        }
    }

    private static bool ClearCacheFolder(string serviceName, string folderPath, List<string> log)
    {
        bool ok = true;
        log.Add(StopLogged(serviceName, out _));

        try
        {
            if (Directory.Exists(folderPath))
            {
                int cleared = 0, failed = 0;
                foreach (var entry in Directory.EnumerateFileSystemEntries(folderPath))
                {
                    try
                    {
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                        else File.Delete(entry);
                        cleared++;
                    }
                    catch
                    {
                        failed++; // in use by another process, or a race with the service - skip it, don't abort the rest
                    }
                }
                log.Add(failed == 0
                    ? $"[{DateTime.Now:T}] Cleared {cleared} item(s) from \"{folderPath}\"."
                    : $"[{DateTime.Now:T}] Cleared {cleared} item(s) from \"{folderPath}\"; {failed} item(s) couldn't be removed (still in use).");
                ok = failed == 0;
            }
            else
            {
                log.Add($"[{DateTime.Now:T}] \"{folderPath}\" doesn't exist - nothing to clear.");
            }
        }
        catch (Exception ex)
        {
            log.Add($"[{DateTime.Now:T}] Couldn't clear \"{folderPath}\": {ex.Message}");
            ok = false;
        }

        log.Add(StartLogged(serviceName, out bool started));
        return ok && started;
    }

    #endregion

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's historical non-nullable shape (-1 for a
    /// timed-out run).</summary>
    private static async Task<(string Output, int ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs)
    {
        var (output, exitCode) = await ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
        return (output, exitCode ?? -1);
    }
}
