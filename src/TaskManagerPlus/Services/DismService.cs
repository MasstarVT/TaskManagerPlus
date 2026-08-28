using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #782-785: every DISM component-store/health-check subcommand this chunk needs
/// (/AnalyzeComponentStore, /StartComponentCleanup[/ResetBase], /CheckHealth, /ScanHealth,
/// /RestoreHealth, /Get-WimInfo) funnels through one shared shell-out helper (RunDismAsync) rather
/// than five separate process-invocation implementations, per this chunk's own instructions -
/// they're all just dism.exe with different arguments and the same progress-bar/log-tail needs.
/// Sibling to WindowsServicingService (which already shells `dism /get-packages`) rather than
/// folded into it, since that file's #770/#773 concerns are servicing-package/upgrade-log reads,
/// not the component-store-health family this chunk owns.
/// </summary>
public static class DismService
{
    private const string DismLogPath = @"C:\Windows\Logs\DISM\dism.log";

    private static readonly Regex ProgressPercentRegex = new(@"(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);
    private static readonly Regex ErrorCodeRegex = new(@"[Ee]rror:\s*(0x[0-9A-Fa-f]+)", RegexOptions.Compiled);

    #region #782 - Component store analysis

    public static async Task<ComponentStoreAnalysis> AnalyzeComponentStoreAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (output, exitCode) = await RunDismAsync("/Online /Cleanup-Image /AnalyzeComponentStore", null, 5 * 60 * 1000, cancellationToken).ConfigureAwait(false);
            bool success = exitCode == 0;
            return new ComponentStoreAnalysis
            {
                Success = success,
                ErrorText = success ? null : ExtractLastNonEmptyLine(output),
                ActualSizeText = ExtractField(output, "Actual Size of Component Store"),
                SharedWithWindowsText = ExtractField(output, "Shared with Windows"),
                BackupsAndDisabledFeaturesText = ExtractField(output, "Backups and Disabled Features"),
                CacheAndTempDataText = ExtractField(output, "Cache and Temporary Data"),
                DateOfLastCleanupText = ExtractField(output, "Date of Last Cleanup"),
                ReclaimablePackageCount = ExtractIntField(output, "Number of Reclaimable Packages"),
                CleanupRecommended = ExtractYesNoField(output, "Component Store Cleanup Recommended"),
                RawOutput = output,
            };
        }
        catch (Exception ex)
        {
            return new ComponentStoreAnalysis { Success = false, ErrorText = ex.Message };
        }
    }

    #endregion

    #region #783 - Component store cleanup runner

    /// <summary>#783: plain `/StartComponentCleanup` (reversible - Windows can still uninstall any
    /// currently installed update afterward) when resetBase is false, or `/StartComponentCleanup
    /// /ResetBase` (permanent - see this chunk's ViewModel/View for the distinct, clearly-labelled
    /// confirmation each gets) when true. Progress streamed via `progress` from DISM's own
    /// percentage output.</summary>
    public static async Task<(bool Success, string Output, int ExitCode)> StartComponentCleanupAsync(bool resetBase, IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        string args = resetBase
            ? "/Online /Cleanup-Image /StartComponentCleanup /ResetBase"
            : "/Online /Cleanup-Image /StartComponentCleanup";
        try
        {
            var (output, exitCode) = await RunDismAsync(args, progress, 30 * 60 * 1000, cancellationToken).ConfigureAwait(false);
            return (exitCode == 0, output, exitCode);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, -1);
        }
    }

    #endregion

    #region #784 - DISM health scans

    public static Task<DismHealthScanResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        => RunHealthScanAsync(DismHealthOperation.CheckHealth, null, null, 2 * 60 * 1000, cancellationToken);

    public static Task<DismHealthScanResult> ScanHealthAsync(IProgress<int>? progress, CancellationToken cancellationToken = default)
        => RunHealthScanAsync(DismHealthOperation.ScanHealth, null, progress, 30 * 60 * 1000, cancellationToken);

    /// <summary>#784/#785: sourceArg is the full `/Source:WIM:"&lt;path&gt;":&lt;index&gt;
    /// /LimitAccess` argument built by #785's repair-source picker, or null for a plain
    /// RestoreHealth attempt against Windows Update.</summary>
    public static Task<DismHealthScanResult> RestoreHealthAsync(string? sourceArg, IProgress<int>? progress, CancellationToken cancellationToken = default)
        => RunHealthScanAsync(DismHealthOperation.RestoreHealth, sourceArg, progress, 30 * 60 * 1000, cancellationToken);

    private static async Task<DismHealthScanResult> RunHealthScanAsync(DismHealthOperation op, string? sourceArg, IProgress<int>? progress, int timeoutMs, CancellationToken cancellationToken)
    {
        string args = op switch
        {
            DismHealthOperation.CheckHealth => "/Online /Cleanup-Image /CheckHealth",
            DismHealthOperation.ScanHealth => "/Online /Cleanup-Image /ScanHealth",
            DismHealthOperation.RestoreHealth => "/Online /Cleanup-Image /RestoreHealth" + (string.IsNullOrEmpty(sourceArg) ? string.Empty : " " + sourceArg),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };

        var sw = Stopwatch.StartNew();
        string output;
        int exitCode;
        try
        {
            (output, exitCode) = await RunDismAsync(args, progress, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DismHealthScanResult { Operation = op, Success = false, Summary = $"Couldn't run DISM: {ex.Message}" };
        }
        sw.Stop();

        bool success = exitCode == 0;
        string? errorCode = ExtractErrorCode(output);
        bool needsSource = op == DismHealthOperation.RestoreHealth && !success &&
            string.Equals(errorCode, "0x800f081f", StringComparison.OrdinalIgnoreCase);

        return new DismHealthScanResult
        {
            Operation = op,
            Success = success,
            ExitCode = exitCode,
            IsRepairable = output.Contains("is repairable", StringComparison.OrdinalIgnoreCase) ? true
                : (output.Contains("No component store corruption", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("did not find any component store corruption", StringComparison.OrdinalIgnoreCase)) ? false
                : null,
            Summary = ExtractLastNonEmptyLine(output),
            RawOutput = output,
            DurationSeconds = sw.Elapsed.TotalSeconds,
            NeedsRepairSource = needsSource,
            ErrorCode = errorCode,
            DismLogTail = success ? null : ReadDismLogTail(),
        };
    }

    /// <summary>#784: "tail of C:\Windows\Logs\DISM\dism.log shown on failure" - dism.log itself
    /// can grow to tens of MB across many runs, so this seeks near the end (bounded by maxBytes)
    /// rather than reading the whole file, the same "don't load a huge log fully" tradeoff #786's
    /// CBS.log extraction below takes.</summary>
    private static string? ReadDismLogTail(int maxLines = 80, int maxBytes = 2 * 1024 * 1024)
    {
        try
        {
            if (!File.Exists(DismLogPath)) return null;
            using var stream = new FileStream(DismLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
            return string.Join(Environment.NewLine, lines.TakeLast(maxLines));
        }
        catch
        {
            return null; // log missing/locked/access denied - the DISM output above still stands on its own
        }
    }

    #endregion

    #region #785 - RestoreHealth source picker (Get-WimInfo)

    /// <summary>#785: `dism /Get-WimInfo /WimFile:&lt;path&gt;` - the index list the user picks
    /// from so the /Source argument names the matching edition rather than guessing index 1.</summary>
    public static async Task<(bool Success, List<WimImageInfo> Images, string? Error)> GetWimInfoAsync(string wimPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var (output, exitCode) = await RunDismAsync($"/Get-WimInfo /WimFile:\"{wimPath}\"", null, 60000, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0) return (false, new List<WimImageInfo>(), ExtractLastNonEmptyLine(output));

            var images = new List<WimImageInfo>();
            int? index = null;
            string? name = null, desc = null, size = null;

            void Flush()
            {
                if (index is { } i)
                    images.Add(new WimImageInfo { Index = i, Name = name ?? string.Empty, Description = desc ?? string.Empty, SizeText = size ?? string.Empty });
                index = null; name = null; desc = null; size = null;
            }

            foreach (var raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();
                var im = Regex.Match(line, @"^Index\s*:\s*(\d+)$");
                if (im.Success) { Flush(); index = int.Parse(im.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
                var nm = Regex.Match(line, @"^Name\s*:\s*(.*)$");
                if (nm.Success) { name = nm.Groups[1].Value.Trim(); continue; }
                var dm = Regex.Match(line, @"^Description\s*:\s*(.*)$");
                if (dm.Success) { desc = dm.Groups[1].Value.Trim(); continue; }
                var sm = Regex.Match(line, @"^Size\s*:\s*(.*)$");
                if (sm.Success) { size = sm.Groups[1].Value.Trim(); continue; }
            }
            Flush();

            return (images.Count > 0, images, images.Count == 0 ? "Couldn't find any image indexes in Get-WimInfo's output - is this a valid install.wim/install.esd?" : null);
        }
        catch (Exception ex)
        {
            return (false, new List<WimImageInfo>(), ex.Message);
        }
    }

    #endregion

    #region Shared DISM shell-out + output parsing

    /// <summary>
    /// The one shell-out helper #782-785 all funnel through. DISM updates its own progress bar in
    /// place using carriage returns rather than newlines (`[===   30.0%]`, repeated over the same
    /// line), so a plain line-by-line ReadLineAsync loop would never observe any progress until the
    /// whole operation finished - this reads raw characters instead and splits on either \r or \n,
    /// feeding each resulting chunk through the percentage regex below and into `progress`. Bounded
    /// by a generous timeout (30 minutes for ScanHealth/RestoreHealth/cleanup, since those can
    /// legitimately run for several minutes on a large component store) plus the caller's own
    /// CancellationToken (wired to a Cancel button in the ViewModel) so a truly hung dism.exe can
    /// still be killed.
    /// </summary>
    private static async Task<(string Output, int ExitCode)> RunDismAsync(string args, IProgress<int>? progress, int timeoutMs, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("dism.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start dism.exe");

        var fullOutput = new StringBuilder();
        var errorTask = proc.StandardError.ReadToEndAsync();

        var readTask = Task.Run(async () =>
        {
            var buffer = new char[512];
            var chunk = new StringBuilder();
            int read;
            while ((read = await proc.StandardOutput.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    char c = buffer[i];
                    if (c is '\r' or '\n')
                    {
                        if (chunk.Length > 0)
                        {
                            string line = chunk.ToString();
                            fullOutput.AppendLine(line);
                            ReportProgress(line, progress);
                            chunk.Clear();
                        }
                    }
                    else
                    {
                        chunk.Append(c);
                    }
                }
            }
            if (chunk.Length > 0)
            {
                string line = chunk.ToString();
                fullOutput.AppendLine(line);
                ReportProgress(line, progress);
            }
        });

        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await Task.WhenAll(readTask, proc.WaitForExitAsync(linkedCts.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            string partial = fullOutput.ToString();
            return (partial.Length > 0 ? partial : "(dism timed out or was cancelled)", -1);
        }

        string errText = await errorTask.ConfigureAwait(false);
        return (fullOutput.ToString() + errText, proc.ExitCode);
    }

    private static void ReportProgress(string line, IProgress<int>? progress)
    {
        if (progress is null) return;
        var m = ProgressPercentRegex.Match(line);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out double pct))
            progress.Report((int)Math.Clamp(pct, 0, 100));
    }

    private static string? ExtractField(string output, string label)
    {
        var m = Regex.Match(output, Regex.Escape(label) + @"\s*:\s*(.+)");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static int? ExtractIntField(string output, string label)
    {
        var text = ExtractField(output, label);
        return text is not null && int.TryParse(text.Trim(), out int v) ? v : null;
    }

    private static bool? ExtractYesNoField(string output, string label)
    {
        var text = ExtractField(output, label);
        if (text is null) return null;
        if (text.StartsWith("Yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("No", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    private static string? ExtractErrorCode(string output)
    {
        var m = ErrorCodeRegex.Match(output);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string ExtractLastNonEmptyLine(string output)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("[", StringComparison.Ordinal)).ToList();
        return lines.Count > 0 ? lines[^1] : "(no output)";
    }

    #endregion
}
