using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #356/#357/#358/#360: "what's eating my disk / how do I get space back" facts - component-store
/// (WinSxS) analysis and cleanup, a reclaimable-space inventory (Windows.old, temp folders,
/// Delivery Optimization cache, crash dumps, WER, Prefetch, Recycle Bin) plus the current Storage
/// Sense policy, hibernation file sizing, and the search indexer's on-disk footprint - all shown
/// together in the Storage tab's "Reclaimable space" card. Every read here degrades to Unknown/
/// empty/hidden on failure rather than fabricating a number, same as every other WMI/registry/
/// shell-out fact in this app. Shares the concurrent-read + bounded-wait + Kill()-on-timeout
/// process pattern VolumeDiagnosticsService's fsutil/vssadmin calls already use.
/// </summary>
public static class ReclaimableSpaceService
{
    /// <summary>#1084: delegates to the shared <see cref="ToolRunner"/>, keeping this service's
    /// degrade-to-empty shape (empty output, Completed=false on timeout or start failure).</summary>
    private static async Task<(string Output, bool Completed)> RunProcessAsync(string exe, string arguments, int timeoutMs)
    {
        try
        {
            var (output, exitCode) = await ToolRunner.RunCapturedAsync(exe, arguments, timeoutMs, timeoutOutput: string.Empty);
            return exitCode is null ? (string.Empty, false) : (output, true);
        }
        catch
        {
            return (string.Empty, false);
        }
    }

    // ================================================================================
    // #356: dism /Online /Cleanup-Image /AnalyzeComponentStore + /StartComponentCleanup.
    // ================================================================================

    private static readonly Regex ActualSizeRegex = new(@"Actual Size of Component Store\s*:\s*([\d.,]+)\s*(B|KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SharedSizeRegex = new(@"Shared with Windows\s*:\s*([\d.,]+)\s*(B|KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BackupsSizeRegex = new(@"Backups and Disabled Features\s*:\s*([\d.,]+)\s*(B|KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CacheSizeRegex = new(@"Cache and Temporary Data\s*:\s*([\d.,]+)\s*(B|KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateBasedCleanupRegex = new(@"Date Based Cleanup Availability\s*:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CleanupRecommendedRegex = new(@"Component Store Cleanup (?:is )?Recommended\s*:\s*(Yes|No)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Slow (walks the whole WinSxS store) - on-demand button only, never run on a tick,
    /// per this round's brief. Every numeric field is best-effort parsed from dism's own text
    /// report (RawText is kept in full so a field this Windows build phrases differently is still
    /// readable) rather than reimplementing WinSxS's own accounting.</summary>
    public static async Task<ComponentStoreAnalysis> AnalyzeComponentStoreAsync()
    {
        var (output, completed) = await RunProcessAsync("dism.exe", "/Online /Cleanup-Image /AnalyzeComponentStore", 300_000);
        if (!completed || output.Length == 0)
            return new ComponentStoreAnalysis { Available = false, UnavailableReason = "dism.exe did not complete (timed out, or couldn't be started - this requires an elevated process, which this app already is)." };

        return new ComponentStoreAnalysis
        {
            Available = true,
            ActualStoreSizeBytes = ParseSizeMatch(ActualSizeRegex, output),
            SharedWithWindowsBytes = ParseSizeMatch(SharedSizeRegex, output),
            BackupsAndDisabledFeaturesBytes = ParseSizeMatch(BackupsSizeRegex, output),
            CacheAndTempDataBytes = ParseSizeMatch(CacheSizeRegex, output),
            CleanupRecommended = CleanupRecommendedRegex.Match(output) is { Success: true } m && m.Groups[1].Value.Equals("Yes", StringComparison.OrdinalIgnoreCase),
            DateBasedCleanupNote = DateBasedCleanupRegex.Match(output) is { Success: true } dm ? dm.Groups[1].Value.Trim() : string.Empty,
            RawText = output.Trim(),
        };
    }

    /// <summary>`/StartComponentCleanup` - removes superseded component versions dism itself has
    /// already determined are safe to drop (older servicing generations, disabled-feature payloads
    /// past their uninstall window). Can take several minutes; the caller is expected to confirm
    /// first, same as this app's other maintenance actions (chkdsk repair, USN journal delete).
    /// </summary>
    public static async Task<(bool Success, string Message)> StartComponentCleanupAsync()
    {
        var (output, completed) = await RunProcessAsync("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", 600_000);
        if (!completed)
            return (false, "Cleanup did not complete (timed out or couldn't be started).");
        bool success = output.Contains("The operation completed successfully", StringComparison.OrdinalIgnoreCase);
        return (success,
            success
                ? "Component cleanup completed. Re-run Analyze above to see the updated size."
                : "Cleanup finished but dism didn't report success - see %windir%\\Logs\\DISM\\dism.log for detail.");
    }

    // ================================================================================
    // #357: reclaimable-space inventory + Storage Sense policy.
    // ================================================================================

    /// <summary>Sums every file under <paramref name="path"/> (any depth, reparse points not
    /// followed - same SafeEnumerateDirectories exclusion LargestItemsService.Scan already applies)
    /// via the same access-denied-skips-the-subtree safe enumeration FileVerificationService's
    /// full-tree walk uses. Null (not 0) when the folder doesn't exist at all, so the UI can show
    /// "not present" rather than a real zero-byte folder.</summary>
    private static long? DirectorySizeOrNull(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return null;
            long total = 0;
            foreach (var file in LargestItemsService.SafeEnumerateFilesRecursive(new DirectoryInfo(path), CancellationToken.None))
            {
                try { total += file.Length; }
                catch { /* file vanished mid-enumeration - skip it */ }
            }
            return total;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One-time-at-tab-load read (per this round's brief - not gated behind a button)
    /// totalling the usual "why is my disk full" suspects, each with a plain-language note on what
    /// it actually is. Expands VolumeDiagnosticsService.ReadRecycleBinBytes (previously a single
    /// number for whichever one volume asked) into a system-wide total across every fixed
    /// drive.</summary>
    public static List<ReclaimableSpaceItem> InventoryReclaimableSpace()
    {
        var items = new List<ReclaimableSpaceItem>();
        string windir = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        string systemRoot = Path.GetPathRoot(windir) ?? "C:\\";

        void Add(string name, string path, string note)
            => items.Add(new ReclaimableSpaceItem { Name = name, Path = path, Note = note, SizeBytes = DirectorySizeOrNull(path) });

        Add("Windows.old", Path.Combine(systemRoot, "Windows.old"),
            "The previous Windows installation, kept for a limited window after an upgrade so you can roll back. Safe to remove (via Settings > System > Storage, or Disk Cleanup's \"Previous Windows installation(s)\") once you're sure you won't roll back.");
        Add("Windows Update download cache", Path.Combine(windir, "SoftwareDistribution", "Download"),
            "Downloaded update packages Windows Update no longer needs once installed. Safe to clear - Windows re-downloads anything it still needs.");
        Add("Delivery Optimization cache", Path.Combine(windir, "SoftwareDistribution", "DeliveryOptimization", "Cache"),
            "Peer-to-peer Windows/Store update cache shared with other PCs on your network/internet. Safe to clear.");
        Add("Temp (current user)", Path.GetTempPath(),
            "Per-user temporary files most apps clean up themselves, but often don't. Usually safe to clear once nothing is actively running.");
        Add("Temp (system-wide)", Path.Combine(windir, "Temp"),
            "System-wide temporary files - same caveats as the per-user Temp folder above.");
        Add("Crash dumps", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
            "Per-app crash minidumps kept for post-mortem debugging. Safe to remove unless you're actively diagnosing a recent crash.");
        Add("Error reporting queue", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER", "ReportQueue"),
            "Windows Error Reporting reports waiting to be sent/reviewed.");
        Add("Error reporting archive", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER", "ReportArchive"),
            "Previously-sent Windows Error Reporting reports kept locally for reference.");
        Add("Prefetch", Path.Combine(windir, "Prefetch"),
            "Boot/app-launch acceleration data Windows rebuilds automatically. Safe to clear, though it costs a brief re-learning period for launch times.");

        long recycleBinTotal = 0;
        bool anyRecycleBin = false;
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var bytes = VolumeDiagnosticsService.ReadRecycleBinBytes(d.Name);
            if (bytes is { } b) { recycleBinTotal += b; anyRecycleBin = true; }
        }
        items.Add(new ReclaimableSpaceItem
        {
            Name = "Recycle Bin (all fixed drives)",
            Path = string.Empty,
            Note = "Deleted files, still recoverable until the bin is emptied.",
            SizeBytes = anyRecycleBin ? recycleBinTotal : null,
        });

        return items;
    }

    /// <summary>Only the well-documented master-enable value ("01") under
    /// HKCU\...\StorageSense\Parameters\StoragePolicy is asserted with confidence - see
    /// StorageSensePolicyInfo's remarks for why every other value found there is surfaced as a raw
    /// name/value pair instead of a guessed meaning.</summary>
    public static StorageSensePolicyInfo ReadStorageSensePolicy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy");
            if (key is null)
                return new StorageSensePolicyInfo { Available = true, Enabled = false, RawPolicyValues = Array.Empty<string>() };

            bool enabled = key.GetValue("01") is int e && e != 0;
            var raw = new List<string>();
            foreach (var valueName in key.GetValueNames())
            {
                if (valueName == "01") continue;
                raw.Add($"{valueName} = {key.GetValue(valueName)}");
            }
            return new StorageSensePolicyInfo { Available = true, Enabled = enabled, RawPolicyValues = raw };
        }
        catch
        {
            return new StorageSensePolicyInfo { Available = false };
        }
    }

    // ================================================================================
    // #358: hibernation file sizing.
    // ================================================================================

    /// <summary>`powercfg /a` lists sleep states available vs. not available on this system, each
    /// with a one-line reason - Hibernate appears in the "not available" section with a reason like
    /// "Hibernation has not been enabled" when it's off, so this only counts it as enabled when the
    /// word appears before that "not available" marker.</summary>
    public static async Task<HibernationInfo> ReadHibernationInfoAsync()
    {
        var (output, completed) = await RunProcessAsync("powercfg.exe", "/a", 15_000);
        if (!completed)
            return new HibernationInfo { Available = false, UnavailableReason = "powercfg.exe did not respond." };

        int splitIndex = output.IndexOf("following sleep states are not available", StringComparison.OrdinalIgnoreCase);
        string availableSection = splitIndex >= 0 ? output[..splitIndex] : output;
        bool enabled = Regex.IsMatch(availableSection, @"^\s*Hibernate\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        long? hiberSize = null;
        try
        {
            string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            string hiberPath = Path.Combine(systemDrive + "\\", "hiberfil.sys");
            if (File.Exists(hiberPath)) hiberSize = new FileInfo(hiberPath).Length;
        }
        catch { /* hidden system file may not be enumerable even to this elevated process - leave null */ }

        int? sizePercent = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            if (key?.GetValue("HiberFileSizePercent") is int pct) sizePercent = pct;
        }
        catch { /* leave null - "Windows default", not a guessed percentage */ }

        return new HibernationInfo
        {
            Available = true,
            Enabled = enabled,
            HiberFileSizeBytes = hiberSize,
            HiberFileSizePercent = sizePercent,
            RawText = output.Trim(),
        };
    }

    /// <summary>Disabling hibernation also disables Fast Startup (which is implemented on top of
    /// hibernation) - the caller is expected to state that consequence in the UI before this runs,
    /// per this round's brief.</summary>
    public static async Task<(bool Success, string Message)> DisableHibernationAsync()
    {
        var (_, completed) = await RunProcessAsync("powercfg.exe", "/hibernate off", 15_000);
        return (completed,
            completed
                ? "Hibernation disabled (Fast Startup is now disabled too, since it depends on hibernation)."
                : "Failed to disable hibernation.");
    }

    public static async Task<(bool Success, string Message)> EnableHibernationAsync()
    {
        var (_, completed) = await RunProcessAsync("powercfg.exe", "/hibernate on", 15_000);
        return (completed, completed ? "Hibernation enabled." : "Failed to enable hibernation.");
    }

    /// <summary>`/hibernate /size &lt;percent&gt;` - also turns hibernation on if it was off.
    /// <paramref name="percent"/> is a percentage of installed RAM (Windows accepts roughly
    /// 40-100); out-of-range values are passed through to powercfg as-is and its own error message
    /// (if any) is returned rather than this app second-guessing the valid range.</summary>
    public static async Task<(bool Success, string Message)> SetHibernateFileSizeAsync(int percent)
    {
        var (output, completed) = await RunProcessAsync("powercfg.exe", $"/hibernate /size {percent}", 15_000);
        if (!completed) return (false, "Failed to set hibernation file size.");
        return output.Trim().Length == 0
            ? (true, $"Hibernation file size set to {percent}% of RAM.")
            : (false, output.Trim());
    }

    // ================================================================================
    // #360: search indexer footprint.
    // ================================================================================

    /// <summary>Windows.edb size + the WSearch service's live state, plus a best-effort indexing
    /// backlog count from HKLM\SOFTWARE\Microsoft\Windows Search\Gather - Windows doesn't expose a
    /// reliably-named backlog DWORD there on every build, so BacklogAvailable stays false (never a
    /// fabricated count) unless a recognizable value is actually found.</summary>
    public static IndexerFootprintInfo ReadIndexerFootprint()
    {
        long? edbSize = null;
        try
        {
            string edbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Search", "Data", "Applications", "Windows", "Windows.edb");
            if (File.Exists(edbPath)) edbSize = new FileInfo(edbPath).Length;
        }
        catch { /* leave null */ }

        string status = "Unknown";
        try
        {
            using var sc = new ServiceController("WSearch");
            status = sc.Status.ToString();
        }
        catch { /* service missing (Windows Search feature removed) or inaccessible */ }

        string startType = "Unknown";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WSearch");
            if (key?.GetValue("Start") is int start)
            {
                startType = start switch
                {
                    2 => "Automatic",
                    3 => "Manual",
                    4 => "Disabled",
                    _ => "Unknown",
                };
            }
        }
        catch { /* leave "Unknown" */ }

        bool backlogAvailable = false;
        long backlog = 0;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Search\Gather\Windows\SystemIndex");
            if (key is not null)
            {
                foreach (var name in key.GetValueNames())
                {
                    if (!name.Contains("backlog", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("itemstoindex", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (key.GetValue(name) is int count)
                    {
                        backlog = count;
                        backlogAvailable = true;
                        break;
                    }
                }
            }
        }
        catch { /* leave unavailable */ }

        return new IndexerFootprintInfo
        {
            EdbSizeBytes = edbSize,
            ServiceStatus = status,
            ServiceStartType = startType,
            BacklogAvailable = backlogAvailable,
            BacklogItemCount = backlog,
        };
    }

    // ---- shared parsing helper -------------------------------------------------------------

    private static long? ParseSizeMatch(Regex regex, string text)
    {
        var m = regex.Match(text);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out double amount))
            return null;
        return (long)(amount * UnitMultiplier(m.Groups[2].Value));
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
