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

    /// <summary>Media type for one drive letter ("C", no colon) - "HDD"/"SSD"/"SCM"/"Unknown".
    /// Same associator chain as SystemSpecsService.ReadPageFileLocation, generalized to any
    /// drive letter rather than just the page file's.</summary>
    public static string GetMediaType(string driveLetter)
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
                            if (phys["MediaType"] is null) continue;
                            return Convert.ToInt32(phys["MediaType"]) switch
                            {
                                3 => "HDD",
                                4 => "SSD",
                                5 => "SCM",
                                _ => "Unknown",
                            };
                        }
                    }
                }
            }
        }
        catch { /* fall through */ }
        return "Unknown";
    }

    private static string EscapeWmiPath(string objectId) => objectId.Replace(@"\", @"\\").Replace("\"", "\\\"");

    /// <summary>Runs an analyze-only defrag pass and extracts the "Total fragmentation" percentage
    /// from its verbose report. Returns a human-readable status either way - never a raw exception
    /// message, since defrag's own text already explains common cases (SSD, not enough free space
    /// to analyze, ...) better than this app reformatting them would.</summary>
    public static async Task<(bool Success, int? FragmentedPercent, string Message)> Analyze(string driveLetter)
    {
        try
        {
            var psi = new ProcessStartInfo("defrag.exe", $"{driveLetter}: /A /V")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, null, "Couldn't start defrag.exe.");

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
                try { proc.Kill(); } catch { /* best-effort */ }
                return (false, null, "Analysis timed out.");
            }

            string output = (await outputTask) + (await errorTask);

            var match = FragmentationPercentRegex.Match(output);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int percent))
                return (true, percent, $"{percent}% fragmented");

            if (output.Contains("do not need to defragment", StringComparison.OrdinalIgnoreCase))
                return (true, 0, "No significant fragmentation");

            return (true, null, "Analysis completed, but no fragmentation figure was reported.");
        }
        catch (Exception ex)
        {
            return (false, null, $"Analysis failed: {ex.Message}");
        }
    }
}
