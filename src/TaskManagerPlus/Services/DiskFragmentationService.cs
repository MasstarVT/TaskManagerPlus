using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>
/// HDD fragmentation indicator (#86) - not relevant for SSDs (which don't fragment in any way
/// that matters, and defrag.exe itself just runs a TRIM/optimize pass on one instead), so this is
/// scoped to spinning-disk volumes only, detected via the same MSFT_Volume -&gt; MSFT_Partition -&gt;
/// MSFT_Disk -&gt; MSFT_PhysicalDisk.MediaType associator chain SystemSpecsService.ReadPageFileLocation
/// already uses for a different drive letter. Shells out to defrag.exe /A /V (analyze only - never
/// moves data) rather than parsing NTFS's own fragmentation bitmap via native interop, the same
/// "known Windows tool, not raw struct interop" tradeoff ScheduledTaskService and
/// ServiceControlService's recovery-actions reader already take. On-demand only (a button click) -
/// even an analyze-only pass walks the whole volume's MFT and can take a while on a large, busy HDD.
/// </summary>
public static class DiskFragmentationService
{
    private static readonly Regex FragmentationPercentRegex = new(
        @"(?:total|file)\s+fragmentation\s*:\s*(\d+)\s*%", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // #353/#354: best-effort patterns for the MFT-fragmentation and free-space-fragmentation
    // sections of the same /A /V report - field wording has drifted across Windows versions (the
    // fragmentation-percent regex above already only matches some builds' phrasing, per its own
    // "no fragmentation figure was reported" fallback), so every one of these degrades to
    // "not reported" rather than guessed when it doesn't match this build's exact text.
    private static readonly Regex MftSizeRegex = new(
        @"(?:total\s+)?mft\s+size\s*[:=]\s*([\d.,]+)\s*(B|KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MftRecordCountRegex = new(
        @"mft\s+record\s+count\s*[:=]\s*([\d,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MftFragmentsRegex = new(
        @"(?:total\s+)?mft\s+fragment(?:ation|s)?(?:\s+count)?\s*[:=]\s*([\d,]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LargestFreeExtentRegex = new(
        @"largest\s+free\s+space\s+(?:size|extent)\s*[:=]\s*([\d.,]+)\s*(B|KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Media type for one drive letter ("C", no colon) - "HDD"/"SSD"/"SCM"/"Unknown".
    /// Same associator chain as SystemSpecsService.ReadPageFileLocation, generalized to any
    /// drive letter rather than just the page file's.</summary>
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

    /// <summary>Shared associator walk (MSFT_Volume -&gt; MSFT_Partition -&gt; MSFT_Disk -&gt;
    /// MSFT_PhysicalDisk) behind GetMediaType/GetPhysicalSectorSizeBytes - extracted so #345 doesn't
    /// duplicate the four-level ASSOCIATORS OF chain a second time in a different file. Returns
    /// primitive values (not the ManagementObject itself) so the caller never has to reason about a
    /// COM object outliving the `using` searchers that produced it.</summary>
    private static (int? MediaType, uint? PhysicalSectorSizeBytes) FindPhysicalDiskFacts(string driveLetter)
    {
        try
        {
            using var volSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                $"SELECT ObjectId FROM MSFT_Volume WHERE DriveLetter = '{driveLetter[0]}'");
            foreach (ManagementObject vol in volSearcher.Get())
            {
                using var partitions = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Storage",
                    $"ASSOCIATORS OF {{MSFT_Volume.ObjectId='{EscapeWmiPath((string)vol["ObjectId"])}'}} WHERE AssocClass=MSFT_PartitionToVolume");
                foreach (ManagementObject partition in partitions.Get())
                {
                    using var disks = new ManagementObjectSearcher(
                        @"root\Microsoft\Windows\Storage",
                        $"ASSOCIATORS OF {{MSFT_Partition.ObjectId='{EscapeWmiPath((string)partition["ObjectId"])}'}} WHERE AssocClass=MSFT_PartitionToDisk");
                    foreach (ManagementObject disk in disks.Get())
                    {
                        using var physicalDisks = new ManagementObjectSearcher(
                            @"root\Microsoft\Windows\Storage",
                            $"ASSOCIATORS OF {{MSFT_Disk.ObjectId='{EscapeWmiPath((string)disk["ObjectId"])}'}} WHERE AssocClass=MSFT_DiskToPhysicalDisk");
                        foreach (ManagementObject phys in physicalDisks.Get())
                        {
                            int? mediaType = phys["MediaType"] is null ? null : Convert.ToInt32(phys["MediaType"]);
                            uint? sectorSize = phys["PhysicalSectorSize"] is null ? null : Convert.ToUInt32(phys["PhysicalSectorSize"]);
                            return (mediaType, sectorSize);
                        }
                    }
                }
            }
        }
        catch { /* fall through */ }
        return (null, null);
    }

    private static string EscapeWmiPath(string objectId) => objectId.Replace(@"\", @"\\").Replace("\"", "\\\"");

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
            var (output, exitCode) = await ToolRunner.RunCapturedAsync("defrag.exe", $"{letter}: /A /V", 120_000);
            if (exitCode is null) return new FragmentationAnalysis(false, "Analysis timed out.", null, null, null, null, null);

            int? percent = null;
            string message;
            var match = FragmentationPercentRegex.Match(output);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int p))
            {
                percent = p;
                message = $"{p}% fragmented";
            }
            else if (output.Contains("do not need to defragment", StringComparison.OrdinalIgnoreCase))
            {
                percent = 0;
                message = "No significant fragmentation";
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
        if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out double amount))
            return null;
        return (long)(amount * UnitMultiplier(m.Groups[2].Value));
    }

    private static long? ParseCountMatch(Regex regex, string text)
    {
        var m = regex.Match(text);
        if (!m.Success) return null;
        string digits = m.Groups[1].Value.Replace(",", string.Empty);
        return long.TryParse(digits, out long count) ? count : null;
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
