using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>
/// HDD fragmentation indicator (#86) - not relevant for SSDs (which don't fragment in any way
/// that matters, and defrag.exe itself just runs a TRIM/optimize pass on one instead), so this is
/// scoped to spinning-disk volumes only, detected via MSFT_PhysicalDisk.MediaType for the disk
/// number the volume's MSFT_Partition sits on. Shells out to defrag.exe /A /V (analyze only - never
/// moves data) rather than parsing NTFS's own fragmentation bitmap via native interop, the same
/// "known Windows tool, not raw struct interop" tradeoff ScheduledTaskService and
/// ServiceControlService's recovery-actions reader already take. On-demand only (a button click) -
/// even an analyze-only pass walks the whole volume's MFT and can take a while on a large, busy HDD.
/// </summary>
public static class DiskFragmentationService
{
    // Windows 7 onward prints "Total fragmented space = 7%"; the older "Total fragmentation : 7%"
    // phrasing is still accepted in case some build emits it.
    private static readonly Regex FragmentationPercentRegex = new(
        @"(?:total\s+fragmented\s+space|(?:total|file)\s+fragmentation)\s*[:=]\s*(\d+)\s*%", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // #353/#354: best-effort patterns for the MFT-fragmentation and free-space-fragmentation
    // sections of the same /A /V report - field wording has drifted across Windows versions (the
    // fragmentation-percent regex above already only matches some builds' phrasing, per its own
    // "no fragmentation figure was reported" fallback), so every one of these degrades to
    // "not reported" rather than guessed when it doesn't match this build's exact text.
    //
    // defrag formats these numbers in the user's regional format, so a number can carry either
    // separator ("1.25 GB" / "1,25 GB", "1,234,567" / "1.234.567" / "1 234 567" with a no-break
    // space) - the capture takes all of them and ParseLocaleNumber decides which is which.
    private const string LocaleNumber = @"(\d(?:[\d.,'   ]*\d)?)";
    private static readonly Regex MftSizeRegex = new(
        @"(?:total\s+)?mft\s+size\s*[:=]\s*" + LocaleNumber + @"\s*(B|KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MftRecordCountRegex = new(
        @"mft\s+record\s+count\s*[:=]\s*" + LocaleNumber, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MftFragmentsRegex = new(
        @"(?:total\s+)?mft\s+fragment(?:ation|s)?(?:\s+count)?\s*[:=]\s*" + LocaleNumber, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LargestFreeExtentRegex = new(
        @"largest\s+free\s+space\s+(?:size|extent)\s*[:=]\s*" + LocaleNumber + @"\s*(B|KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Media type for one drive letter ("C", no colon) - "HDD"/"SSD"/"SCM"/"Unknown".</summary>
    public static string GetMediaType(string driveLetter)
    {
        var (mediaType, _) = FindPhysicalDiskFacts(driveLetter);
        return mediaType switch
        {
            3 => "HDD",
            4 => "SSD",
            5 => "SCM",
            _ => "Unknown",
        };
    }

    /// <summary>Round 15, #345: physical sector size (bytes) of the disk backing one drive letter -
    /// same associator walk as GetMediaType above (MSFT_PhysicalDisk.PhysicalSectorSize), reused
    /// rather than re-derived, per this round's brief. Used to flag a cluster (allocation unit) size
    /// smaller than the device's physical sector size, which causes read-modify-write penalties on
    /// every sub-physical-sector write on a 4Kn/512e drive.</summary>
    public static uint? GetPhysicalSectorSizeBytes(string driveLetter)
    {
        var (_, sectorSize) = FindPhysicalDiskFacts(driveLetter);
        return sectorSize;
    }

    /// <summary>Shared lookup (MSFT_Partition.DiskNumber -&gt; MSFT_PhysicalDisk) behind
    /// GetMediaType/GetPhysicalSectorSizeBytes - extracted so #345 doesn't duplicate it a second
    /// time in a different file. Returns
    /// primitive values (not the ManagementObject itself) so the caller never has to reason about a
    /// COM object outliving the `using` searchers that produced it.</summary>
    private static (int? MediaType, uint? PhysicalSectorSizeBytes) FindPhysicalDiskFacts(string driveLetter)
    {
        try
        {
            // There is no MSFT_PartitionToDisk or MSFT_Disk -> MSFT_PhysicalDisk association class,
            // so go by number instead: the partition's DiskNumber is the PhysicalDriveN index,
            // which MSFT_PhysicalDisk exposes as DeviceId.
            using var partSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{driveLetter[0]}'");
            foreach (ManagementObject partition in partSearcher.Get())
            {
                if (partition["DiskNumber"] is null) continue;
                uint diskNumber = Convert.ToUInt32(partition["DiskNumber"]);

                using var physicalDisks = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Storage",
                    $"SELECT MediaType, PhysicalSectorSize FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskNumber}'");
                foreach (ManagementObject phys in physicalDisks.Get())
                {
                    int? mediaType = phys["MediaType"] is null ? null : Convert.ToInt32(phys["MediaType"]);
                    uint? sectorSize = phys["PhysicalSectorSize"] is null ? null : Convert.ToUInt32(phys["PhysicalSectorSize"]);
                    return (mediaType, sectorSize);
                }
            }
        }
        catch { /* fall through */ }
        return (null, null);
    }

    /// <summary>Runs an analyze-only defrag pass and extracts the "Total fragmentation" percentage,
    /// the MFT size/record/fragment counts (#353), and the largest free-space extent (#354) from
    /// its verbose report - one shell-out serves all three features rather than running defrag
    /// three times for the same volume. Returns a human-readable status either way - never a raw
    /// exception message, since defrag's own text already explains common cases (SSD, not enough
    /// free space to analyze, ...) better than this app reformatting them would.</summary>
    public static async Task<FragmentationAnalysis> Analyze(string driveLetter)
    {
        try
        {
            // Accepts either "C" or "C:" - HddVolumes/FragmentationRows populate DriveLetter with
            // a trailing colon already (see StorageViewModel), so this strips one off before
            // re-appending it rather than risking a "C::" argument if a caller passes it either way.
            string letter = driveLetter.TrimEnd(':');
            // System32's defrag.exe by absolute path - a bare name is searched for in this
            // (elevated) app's own folder first.
            var psi = new ProcessStartInfo(ToolRunner.ResolveTool("defrag.exe"), $"{letter}: /A /V")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return new FragmentationAnalysis(false, "Couldn't start defrag.exe.", null, null, null, null, null);

            // Concurrent async reads + a bounded WaitForExitAsync + Kill()-on-timeout - the same
            // pattern TracerouteService.RunAsync uses. The previous version already checked
            // WaitForExit's result and killed the process on timeout, but only *after* the
            // unbounded synchronous ReadToEnd() calls above it had already returned - so a defrag
            // run whose verbose report filled the stdout/stderr pipe buffers before exiting could
            // still deadlock (and never reach the timeout/kill logic at all). Starting both reads
            // and the bounded wait concurrently fixes that ordering.
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(120_000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return new FragmentationAnalysis(false, "Analysis timed out.", null, null, null, null, null);
            }

            string output = (await outputTask) + (await errorTask);

            int? percent = null;
            string message;
            var match = FragmentationPercentRegex.Match(output);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int p))
            {
                percent = p;
                message = $"{p}% fragmented";
            }
            else if (proc.ExitCode != 0)
            {
                // Nothing parsed and defrag reported failure (access denied, volume not supported,
                // optimization service unavailable) - its own text, not a completed analysis.
                string reason = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? string.Empty;
                return new FragmentationAnalysis(false,
                    $"defrag couldn't analyze this volume (exit code 0x{proc.ExitCode:X8}){(reason.Length > 0 ? ": " + reason : ".")}",
                    null, null, null, null, null);
            }
            else if (output.Contains("do not need to defragment", StringComparison.OrdinalIgnoreCase))
            {
                // defrag's own verdict, but no figure to show - leave the percent unreported
                // rather than presenting 0% as measured.
                message = "No significant fragmentation (defrag reported no figure)";
            }
            else
            {
                message = "Analysis completed, but no fragmentation figure was reported.";
            }

            long? mftSize = ParseSizeMatch(MftSizeRegex, output);
            long? mftRecords = ParseCountMatch(MftRecordCountRegex, output);
            long? mftFragmentsRaw = ParseCountMatch(MftFragmentsRegex, output);
            int? mftFragments = mftFragmentsRaw is { } mf ? (int)Math.Min(mf, int.MaxValue) : null;
            long? largestFreeExtent = ParseSizeMatch(LargestFreeExtentRegex, output);

            return new FragmentationAnalysis(true, message, percent, mftSize, mftRecords, mftFragments, largestFreeExtent);
        }
        catch (Exception ex)
        {
            return new FragmentationAnalysis(false, $"Analysis failed: {ex.Message}", null, null, null, null, null);
        }
    }

    private static long? ParseSizeMatch(Regex regex, string text)
    {
        var m = regex.Match(text);
        if (!m.Success) return null;
        if (ParseLocaleNumber(m.Groups[1].Value) is not { } amount) return null;
        return (long)(amount * UnitMultiplier(m.Groups[2].Value));
    }

    private static long? ParseCountMatch(Regex regex, string text)
    {
        var m = regex.Match(text);
        if (!m.Success) return null;
        // A count has no fraction, so every separator in it is a group separator.
        string digits = new(m.Groups[1].Value.Where(char.IsAsciiDigit).ToArray());
        return long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long count) ? count : null;
    }

    /// <summary>A size figure in either separator convention. With both '.' and ',' present the last
    /// one is the decimal point; a separator that repeats is grouping; a single one followed by
    /// exactly three digits is ambiguous ("1,234") and is read the way the current culture would
    /// write it, since defrag formats with the user's regional settings.</summary>
    private static double? ParseLocaleNumber(string raw)
    {
        string s = new(raw.Where(c => char.IsAsciiDigit(c) || c == '.' || c == ',').ToArray());
        int lastDot = s.LastIndexOf('.'), lastComma = s.LastIndexOf(',');
        char? decimalSep;
        if (lastDot >= 0 && lastComma >= 0) decimalSep = lastDot > lastComma ? '.' : ',';
        else if (lastDot < 0 && lastComma < 0) decimalSep = null;
        else
        {
            char sep = lastDot >= 0 ? '.' : ',';
            int pos = Math.Max(lastDot, lastComma);
            if (s.Count(c => c == sep) > 1) decimalSep = null;
            else if (s.Length - pos - 1 == 3)
                decimalSep = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator == sep.ToString() ? null : sep;
            else decimalSep = sep;
        }

        string normalized = decimalSep is { } d
            ? new string(s.Where(c => char.IsAsciiDigit(c) || c == d).ToArray()).Replace(d, '.')
            : new string(s.Where(char.IsAsciiDigit).ToArray());
        return double.TryParse(normalized, System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : null;
    }

    private static double UnitMultiplier(string unit) => unit.ToUpperInvariant() switch
    {
        "B" => 1,
        "KB" => 1024,
        "MB" => 1024d * 1024,
        "GB" => 1024d * 1024 * 1024,
        "TB" => 1024d * 1024 * 1024 * 1024,
        _ => 1,
    };
}

/// <summary>#353/#354: everything DiskFragmentationService.Analyze can pull out of one
/// `defrag &lt;vol&gt; /A /V` report - fields are null (shown as "not reported") rather than 0
/// when this Windows build's report doesn't include or phrase that particular line the way the
/// regex expects, per this app's "degrade, never fabricate" convention.</summary>
public sealed record FragmentationAnalysis(
    bool Success,
    string Message,
    int? FragmentedPercent,
    long? MftSizeBytes,
    long? MftRecordCount,
    int? MftFragmentCount,
    long? LargestFreeExtentBytes);
