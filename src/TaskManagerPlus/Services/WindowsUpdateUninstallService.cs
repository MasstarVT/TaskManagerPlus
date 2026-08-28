using System.Diagnostics;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #780: "uninstall a specific update" - lists removable updates from two sources (Win32_QuickFixEngineering,
/// reused from SystemSpecsService.ReadRecentHotfixes, plus DISM's own "Installed"-state servicing
/// packages, read here directly since #770's WindowsServicingService.ListPackagesAsync uses
/// /format:table and drops the Install Time field this card needs) and removes one via `wusa
/// /uninstall /kb:&lt;n&gt;` (QFE hotfix) or `dism /online /remove-package /packagename:&lt;name&gt;`
/// (servicing package). A genuinely new concern from #769-779's read-only history/health cards - the
/// only actually-destructive, per-item mutating action on the tab besides #778's guided reset - so
/// it gets its own file rather than being folded into WindowsServicingService.
/// </summary>
public static class WindowsUpdateUninstallService
{
    private static readonly Regex KbNumberRegex = new(@"KB(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>#780: combines both sources into one removable-updates list, each row carrying its
    /// install date where known. Gated behind its own button in the ViewModel (a `dism /online
    /// /get-packages` full-detail sweep, run here without /format:table so Install Time survives,
    /// can take several seconds - same cost class as #770's table-format read).</summary>
    public static async Task<List<RemovableUpdateInfo>> ListRemovableUpdatesAsync()
    {
        var hotfixTask = Task.Run(SystemSpecsService.ReadRecentHotfixes);
        var dismTask = ListInstalledDismPackagesAsync();
        await Task.WhenAll(hotfixTask, dismTask);

        var result = new List<RemovableUpdateInfo>();

        foreach (var hotfix in hotfixTask.Result)
        {
            // wusa /uninstall needs a bare KB number - a HotFixID that isn't KB-numbered (e.g. some
            // service-pack-style entries) can't be removed this way, so it's left off the list
            // entirely rather than offering an uninstall action that would just fail.
            var match = KbNumberRegex.Match(hotfix.HotFixId);
            if (!match.Success) continue;

            result.Add(new RemovableUpdateInfo
            {
                Identifier = match.Groups[1].Value,
                DisplayName = string.IsNullOrWhiteSpace(hotfix.Description)
                    ? hotfix.HotFixId
                    : $"{hotfix.HotFixId} - {hotfix.Description}",
                InstalledOn = hotfix.InstalledOn,
                Source = "Quick Fix Engineering",
                IsDismPackage = false,
            });
        }

        result.AddRange(dismTask.Result);

        return result.OrderByDescending(u => u.InstalledOn ?? DateTime.MinValue).ToList();
    }

    /// <summary>`dism /online /get-packages` (no /format:table) - each package block includes
    /// "Install Time" that the table format drops, at the cost of a slower parse over a much larger
    /// text block. Only "Installed"-state packages are returned - Superseded/Permanent/Pending
    /// packages either can't be removed via /remove-package or aren't in a state where doing so
    /// makes sense.</summary>
    private static async Task<List<RemovableUpdateInfo>> ListInstalledDismPackagesAsync()
    {
        try
        {
            string output = await RunCapturedAsync("dism.exe", "/online /get-packages", 180000);
            return ParsePackageBlocks(output);
        }
        catch
        {
            return new List<RemovableUpdateInfo>();
        }
    }

    private static List<RemovableUpdateInfo> ParsePackageBlocks(string output)
    {
        var result = new List<RemovableUpdateInfo>();
        string? identity = null, state = null, installTimeRaw = null;

        void Flush()
        {
            if (identity is { Length: > 0 } && state is not null &&
                state.Equals("Installed", StringComparison.OrdinalIgnoreCase))
            {
                DateTime? installedOn = installTimeRaw is not null && DateTime.TryParse(installTimeRaw, out var dt) ? dt : null;
                result.Add(new RemovableUpdateInfo
                {
                    Identifier = identity,
                    DisplayName = identity,
                    InstalledOn = installedOn,
                    Source = "DISM package",
                    IsDismPackage = true,
                });
            }
            identity = null; state = null; installTimeRaw = null;
        }

        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) { Flush(); continue; }

            int sep = line.IndexOf(':');
            if (sep < 0) continue;
            string label = line[..sep].Trim();
            string value = line[(sep + 1)..].Trim();

            if (label.Equals("Package Identity", StringComparison.OrdinalIgnoreCase)) identity = value;
            else if (label.Equals("State", StringComparison.OrdinalIgnoreCase)) state = value;
            else if (label.Equals("Install Time", StringComparison.OrdinalIgnoreCase)) installTimeRaw = value;
        }
        Flush(); // the last block has no trailing blank line to trigger a flush from within the loop

        return result;
    }

    /// <summary>#780: removes one update - `wusa /uninstall /kb:&lt;n&gt; /quiet /norestart` for a
    /// QFE hotfix, `dism /online /remove-package /packagename:&lt;name&gt; /norestart` for a
    /// servicing package. Exit code 3010 (both tools agree on this code) means "succeeded, but a
    /// reboot is needed to finish" - reported back as RebootRequired rather than a failure, so the
    /// caller can show a clear reboot prompt instead of a false "it failed" message. Confirmed by
    /// the caller (WindowsHealthViewModel) with the exact command shown before this runs, matching
    /// CLAUDE.md's "mutating actions require confirmation" rule.</summary>
    public static async Task<(bool Success, bool RebootRequired, string Output)> UninstallAsync(RemovableUpdateInfo update)
    {
        (string exe, string args) = update.IsDismPackage
            ? ("dism.exe", $"/online /remove-package /packagename:{update.Identifier} /norestart")
            : ("wusa.exe", $"/uninstall /kb:{update.Identifier} /quiet /norestart");

        return await RunAndClassifyAsync(exe, args, 300000);
    }

    /// <summary>Same concurrent-read/bounded-wait/kill-on-timeout shell-out pattern as every other
    /// process launched in this app (see ScheduledTaskService/TracerouteService), plus the shared
    /// "0/3010 = success" exit-code convention wusa.exe and dism.exe both use.</summary>
    private static async Task<(bool Success, bool RebootRequired, string Output)> RunAndClassifyAsync(string exe, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return (false, false, $"couldn't start {exe}");

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
            return (false, false, "(command timed out)");
        }

        string output = ((await outputTask) + (await errorTask)).Trim();
        bool rebootRequired = proc.ExitCode == 3010;
        bool success = proc.ExitCode == 0 || rebootRequired;
        return (success, rebootRequired, output);
    }

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
