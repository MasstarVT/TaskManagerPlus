using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>
/// #330: the two non-SMART bad-sector sources this app can read, each independently wrapped to
/// degrade to "not reported" (never a guess) - StorageViewModel combines these with the SMART
/// 05/C5/C6 attributes SmartRawAttributeService already decodes into one BadSectorSummary, shown
/// side by side rather than reconciled when they disagree.
/// </summary>
public static class BadSectorService
{
    // Wininit logs the classic chkdsk summary as event 1001 in the Application log, with the full
    // report text embedded as an insertion string - the same source Explorer's own "check this
    // drive" result reads from.
    private const string ChkdskProviderName = "Microsoft-Windows-Wininit";
    private const int ChkdskSummaryEventId = 1001;

    private static readonly Regex BadSectorsKbRegex = new(
        @"(\d[\d,]*)\s*KB in bad sectors", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Every chkdsk report opens with "Checking file system on C:" - the only thing in the event
    // that says which volume it was about.
    private static readonly Regex CheckedVolumeRegex = new(
        @"Checking file system on\s+(?:\\\?\?\\)?([A-Za-z]):", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MaxChkdskReportsScanned = 50;

    /// <summary>Most recent chkdsk report for volume <paramref name="driveLetter"/> ("C" or "C:")
    /// with a "KB in bad sectors" line, from the Application log. A report for a different volume,
    /// or one that doesn't name its volume, is skipped rather than attributed to this one. Null
    /// (never a fabricated 0) when no such report has been logged - a drive that's simply never
    /// had chkdsk run against it says nothing about whether it has bad sectors, unlike a report
    /// that explicitly says "0 KB in bad sectors".</summary>
    public static (long? BadSectorsKb, DateTime? ReportDate) ReadLatestChkdskBadSectors(string driveLetter)
    {
        string wanted = driveLetter.TrimEnd(':', '\\');
        if (wanted.Length != 1) return (null, null);
        try
        {
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='{ChkdskProviderName}'] and EventID={ChkdskSummaryEventId}]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            for (int scanned = 0; scanned < MaxChkdskReportsScanned && reader.ReadEvent() is { } record; scanned++)
            {
                using (record)
                {
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    var volume = CheckedVolumeRegex.Match(message);
                    if (!volume.Success || !volume.Groups[1].Value.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var match = BadSectorsKbRegex.Match(message);
                    if (match.Success && long.TryParse(match.Groups[1].Value.Replace(",", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out long kb))
                        return (kb, record.TimeCreated);
                }
            }
        }
        catch
        {
            // Provider/log unavailable, or chkdsk has simply never run - "not reported".
        }
        return (null, null);
    }

    // The elevated output format isn't confirmed here, so a "$BadClus ... N clusters" line with no
    // byte figure is accepted too (converted with the volume's cluster size when the caller has it).
    private static readonly Regex BadClusBytesRegex = new(
        @"\$BadClus[^\r\n]*?([\d,]+)\s*bytes", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BadClusClustersRegex = new(
        @"\$BadClus[^\r\n]*?([\d,]+)\s*clusters?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>$BadClus metadata file's currently-allocated size (#330), via
    /// `fsutil volume allocationreport &lt;volume&gt;` - the documented tool for per-metadata-file
    /// allocation, rather than parsing the NTFS bitmap directly. A non-zero $BadClus allocation
    /// means NTFS itself currently has clusters marked bad on this volume - independent of (and can
    /// disagree with) both SMART's drive-level counters and a chkdsk report's point-in-time
    /// finding.</summary>
    public static async Task<long?> ReadBadClusAllocatedBytesAsync(string driveLetter, int bytesPerCluster = 0)
    {
        try
        {
            // fsutil needs the colon: a bare "C" fails with Error 123.
            var psi = new ProcessStartInfo(ToolRunner.ResolveTool("fsutil.exe"), $"volume allocationreport {driveLetter.TrimEnd(':', '\\')}:")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            // Concurrent async reads + a bounded WaitForExitAsync + Kill()-on-timeout - the same
            // pattern TracerouteService.RunAsync/VolumeDiagnosticsService already use elsewhere.
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(15000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return null;
            }

            string output = (await outputTask) + (await errorTask);
            var match = BadClusBytesRegex.Match(output);
            if (match.Success && long.TryParse(match.Groups[1].Value.Replace(",", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes))
                return bytes;

            var clusterMatch = BadClusClustersRegex.Match(output);
            if (bytesPerCluster > 0 && clusterMatch.Success
                && long.TryParse(clusterMatch.Groups[1].Value.Replace(",", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out long clusters))
                return clusters * bytesPerCluster;
        }
        catch
        {
            // fsutil unavailable, or this volume doesn't support an allocation report (FAT32, ...).
        }
        return null;
    }
}
