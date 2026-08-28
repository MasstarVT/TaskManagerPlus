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
    /// gated behind its own explicit button rather than loaded with the rest of the tab.</summary>
    public static async Task<List<ServicingPackageInfo>> ListPackagesAsync()
    {
        var result = new List<ServicingPackageInfo>();
        try
        {
            string output = (await RunCapturedAsync("dism.exe", "/online /get-packages /format:table", 120000)).Output;
            foreach (var rawLine in output.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0 || !line.Contains('|')) continue;
                if (line.StartsWith("---", StringComparison.Ordinal)) continue;
                if (line.StartsWith("Package Identity", StringComparison.OrdinalIgnoreCase)) continue; // header row

                int sep = line.LastIndexOf('|');
                string identity = line[..sep].Trim();
                string state = line[(sep + 1)..].Trim();
                if (identity.Length == 0 || state.Length == 0) continue;

                result.Add(new ServicingPackageInfo { PackageIdentity = identity, State = state });
            }
        }
        catch
        {
            // dism unavailable/failed/timed out - empty list, same as every other optional data
            // source in this app degrades on failure.
        }
        return result;
    }

    #region #773 - Feature update failure analysis

    private static readonly string PantherBtPath = Path.Combine(@"C:\$WINDOWS.~BT\Sources\Panther");
    private static readonly string PantherCommittedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Panther");

    private static readonly Regex HostLineRegex = new(@"^\s*Host:\s*(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex RollbackLineRegex = new(@"^.*Rollback.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MoupgErrorRegex = new(@"^.*MOUPG.*Error.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExtendCodeRegex = new(@"[Ee]xtended?\s*[Cc]ode[:\s]*(0x[0-9A-Fa-f]+)", RegexOptions.Compiled);

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

        var extendMatch = ExtendCodeRegex.Match(text);
        string? extendCode = extendMatch.Success ? extendMatch.Groups[1].Value : null;

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
    private static string? FindSetupDiagPath()
    {
        string[] candidates =
        {
            Path.Combine(PantherBtPath, "SetupDiag.exe"),
            Path.Combine(AppContext.BaseDirectory, "SetupDiag.exe"),
            @"C:\SetupDiag\SetupDiag.exe",
        };
        foreach (var candidate in candidates)
        {
            try { if (File.Exists(candidate)) return candidate; }
            catch { /* ignore this candidate, try the next */ }
        }
        return null;
    }

    /// <summary>#773: runs SetupDiag.exe (when present - see FindSetupDiagPath) against the newest
    /// available upgrade logs and returns its own summarized verdict line, instead of this app's
    /// own regex-based log parse. Never downloads or installs SetupDiag itself - this app takes no
    /// network dependency for this feature, matching CLAUDE.md's "prefer a known Windows tool"
    /// convention without silently fetching a binary from the internet.</summary>
    public static async Task<(bool Success, string? Verdict, string? Error)> RunSetupDiagAsync()
    {
        string? exePath = FindSetupDiagPath();
        if (exePath is null) return (false, null, "SetupDiag.exe wasn't found on this machine.");

        try
        {
            string outputDir = Path.Combine(Path.GetTempPath(), "TaskManagerPlus-SetupDiag");
            Directory.CreateDirectory(outputDir);
            string resultsPath = Path.Combine(outputDir, "SetupDiagResults.log");
            try { File.Delete(resultsPath); } catch { /* best-effort */ }

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
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";

    #endregion

    private static async Task<(string Output, int ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs)
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
            return ("(command timed out)", -1);
        }

        string output = (await outputTask) + (await errorTask);
        return (output, proc.ExitCode);
    }
}
