using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
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

    /// <summary>Most recent chkdsk report's "KB in bad sectors" line, from the Application log.
    /// Null (never a fabricated 0) when no chkdsk report has ever been logged - a drive that's
    /// simply never had chkdsk run against it says nothing about whether it has bad sectors, unlike
    /// a report that explicitly says "0 KB in bad sectors".</summary>
    public static (long? BadSectorsKb, DateTime? ReportDate) ReadLatestChkdskBadSectors()
    {
        try
        {
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='{ChkdskProviderName}'] and EventID={ChkdskSummaryEventId}]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            if (reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; }

                    var match = BadSectorsKbRegex.Match(message);
                    if (match.Success && long.TryParse(match.Groups[1].Value.Replace(",", string.Empty), out long kb))
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

    private static readonly Regex BadClusBytesRegex = new(
        @"\$BadClus[^\r\n]*?([\d,]+)\s*bytes", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>$BadClus metadata file's currently-allocated size (#330), via
    /// `fsutil volume allocationreport &lt;volume&gt;` - the documented tool for per-metadata-file
    /// allocation, rather than parsing the NTFS bitmap directly. A non-zero $BadClus allocation
    /// means NTFS itself currently has clusters marked bad on this volume - independent of (and can
    /// disagree with) both SMART's drive-level counters and a chkdsk report's point-in-time
    /// finding.</summary>
    public static async Task<long?> ReadBadClusAllocatedBytesAsync(string driveLetter)
    {
        try
        {
            var psi = new ProcessStartInfo("fsutil.exe", $"volume allocationreport {driveLetter}")
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
                try { proc.Kill(); } catch { /* best-effort */ }
                return null;
            }

            string output = (await outputTask) + (await errorTask);
            var match = BadClusBytesRegex.Match(output);
            if (match.Success && long.TryParse(match.Groups[1].Value.Replace(",", string.Empty), out long bytes))
                return bytes;
        }
        catch
        {
            // fsutil unavailable, or this volume doesn't support an allocation report (FAT32, ...).
        }
        return null;
    }
}
