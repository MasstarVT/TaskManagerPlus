using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TaskManagerPlus.Models;
using TaskManagerPlus.ViewModels;

namespace TaskManagerPlus.Services;

/// <summary>
/// Backs the `--dump-json &lt;path&gt;` command-line flag (#77) - a one-shot snapshot of the
/// current metrics as JSON, written to disk and the process exits, no UI shown. Useful for
/// scripted/remote diagnostics (a scheduled task or remote-support script that wants a machine-
/// readable reading without driving the full GUI). Each service is constructed fresh and disposed
/// right after one sample - this is a one-shot CLI path, not something that needs the long-lived
/// sampler objects the running app keeps. The app's elevation requirement (app.manifest) still
/// applies here: launching with this flag still triggers the same UAC prompt as a normal launch.
///
/// suggestions.md #996: `--scan &lt;path&gt;` and `--collect &lt;path&gt;` extend this exact
/// one-shot construction style - see <see cref="ScanAsync"/>/<see cref="CollectAsync"/> below.
/// </summary>
public static class CliDumpService
{
    public static async Task DumpSnapshotAsync(string outputPath)
    {
        using var hardware = new HardwareMonitorService();
        // Rate-based counters (CPU%, disk/network throughput) read 0 on their very first sample
        // immediately after construction even though the constructor primes several of them - a
        // real reading needs one full tick's worth of elapsed time, which a one-shot CLI dump
        // can't wait around for without meaningfully slowing down a scripted caller. This is a
        // known, documented limitation of this snapshot mode, not a bug.
        var snapshot = hardware.Sample();

        var specsService = new SystemSpecsService();
        var specs = await specsService.QueryAsync();

        using var sensors = new SensorMonitorService();
        var readings = sensors.Sample();

        var result = new
        {
            timestamp = DateTime.Now.ToString("O"),
            cpu = new
            {
                name = specs.CpuName,
                percent = snapshot.CpuTotalPercent,
                clockGhz = snapshot.CpuCurrentClockGhz,
                baseClockGhz = snapshot.CpuBaseClockGhz,
                logicalProcessors = snapshot.LogicalProcessors,
                physicalCores = snapshot.PhysicalCores,
            },
            memory = new
            {
                usedBytes = snapshot.RamUsedBytes,
                totalBytes = snapshot.RamTotalBytes,
                percent = snapshot.RamPercent,
            },
            disk = new
            {
                activePercent = snapshot.DiskActivePercent,
                readBytesPerSec = snapshot.DiskReadBytesPerSec,
                writeBytesPerSec = snapshot.DiskWriteBytesPerSec,
            },
            network = new
            {
                receiveBytesPerSec = snapshot.NetworkReceiveBytesPerSec,
                sendBytesPerSec = snapshot.NetworkSendBytesPerSec,
            },
            sensors = readings
                .Where(r => r.Value.HasValue)
                .Select(r => new { hardware = r.HardwareName, sensor = r.SensorName, type = r.Type.ToString(), value = r.Value!.Value })
                .ToList(),
            system = new
            {
                os = specs.OsName,
                model = $"{specs.Manufacturer} {specs.Model}".Trim(),
                ramTotalBytes = specs.RamTotalBytes,
            },
        };

        ThrowIfUnsafeOutput(outputPath, ".json");
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>#996: `--scan &lt;path&gt;` - constructs the minimum ViewModels
    /// RulesEngineService.BuildMetricBag/Evaluate needs (mirroring MainViewModel's own composition
    /// order, minus everything the rules engine doesn't read), waits briefly for each poller's
    /// first tick (the same "a rate-based counter reads 0 on its very first sample" limitation
    /// DumpSnapshotAsync above already documents and accepts for this one-shot construction style),
    /// evaluates, and writes the findings as JSON.
    ///
    /// `outputPath` is dual-purpose by design: a path ending in ".json" is written to directly (the
    /// #996 ad-hoc/scripted case, matching --dump-json's own contract exactly); anything else is
    /// treated as a base folder and a fresh "&lt;path&gt;\yyyy-MM-dd_HHmmss\findings.json" is
    /// created under it - what #997's nightly scheduled task actually passes (a stable
    /// AppPaths.SettingsDirectory\UnattendedScans argument that must produce a NEW dated folder on
    /// every run, which a static schtasks command line can't do by injecting today's date itself).
    /// </summary>
    public static async Task ScanAsync(string outputPath, bool scrub, bool quiet)
    {
        _ = quiet; // #996: this path already shows no UI/dialogs regardless (same as --dump-json) - kept for CLI-contract symmetry with --collect.

        using var performance = new PerformanceViewModel();
        using var energyThermals = new EnergyThermalsViewModel(performance);
        var systemSpecs = new SystemSpecsViewModel();
        using var services = new ServicesViewModel();
        var processHistory = new ProcessHistoryService();
        using var leakWatch = new LeakWatchViewModel();
        using var processes = new ProcessesViewModel(processHistory, leakWatch);
        using var rulesEngine = new RulesEngineService(performance);

        await Task.Delay(2500);
        await WaitForSpecsAsync(systemSpecs);

        var bag = RulesEngineService.BuildMetricBag(performance, energyThermals, systemSpecs, services, processes, out var unavailable);
        var result = rulesEngine.Evaluate(bag, unavailable);
        var findings = SummaryViewModel.SortIssues(new List<HealthIssue>(result.Findings), HealthFindingSortMode.Impact);

        string json = JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true });
        if (scrub) json = await ScrubTextAsync(json);

        string resolvedPath = ResolveScanOutputPath(outputPath);
        ThrowIfUnsafeOutput(resolvedPath, ".json");
        var dir = Path.GetDirectoryName(Path.GetFullPath(resolvedPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(resolvedPath, json);
    }

    /// <summary>SystemSpecsViewModel loads its inventory asynchronously and usually takes longer
    /// than the fixed first-tick wait; rules evaluated before then report those metrics as
    /// "couldn't check". Waits (bounded) while that load is still running - success or failure.</summary>
    private static async Task WaitForSpecsAsync(SystemSpecsViewModel specs)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (specs.IsLoading && !specs.HasLoaded && DateTime.UtcNow < deadline)
            await Task.Delay(250);
    }

    /// <summary>Every CLI output path (`--dump-json`, `--scan`, `--collect`, and the
    /// `&lt;path&gt;.error.txt` written when one fails) comes from a command line, and a per-user
    /// Start-menu or taskbar shortcut - editable without elevation - can append these flags to the
    /// user's ordinary launch, whose UAC prompt looks the same. The write then happens elevated (or
    /// as SYSTEM, for the nightly scan), so refuse what that shortcut edit would otherwise buy:
    /// writing into the Windows, Program Files or ProgramData folders (this app's own settings
    /// folder excepted - a portable copy installed under Program Files keeps its UnattendedScans
    /// there), overwriting an existing file that isn't one of <paramref name="allowedExtensions"/>,
    /// device-namespace, admin-share and alternate-stream spellings that would dodge the folder
    /// check, and anything reached through a junction or symbolic link a non-admin controls
    /// (<see cref="ThrowIfRedirected"/>). Check-then-write, so a swap in between is still
    /// conceivable; this closes the plain shortcut edit.</summary>
    internal static void ThrowIfUnsafeOutput(string path, params string[] allowedExtensions)
    {
        string full = NormalizeForComparison(path);
        if (full.IndexOf(':', 2) >= 0)
            throw new IOException($"Refusing to write {path}: an alternate data stream isn't an output location.");
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server\C$\... is just another spelling of that machine's drive root.
            string[] parts = full.TrimStart('\\').Split('\\');
            if (parts.Length < 2 || parts[1].EndsWith('$'))
                throw new IOException($"Refusing to write {path}: administrative shares (C$, ADMIN$, ...) aren't an output location.");
        }

        if (!IsUnder(full, NormalizeForComparison(AppPaths.SettingsDirectory)))
        {
            foreach (var folder in new[]
                     {
                         Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                     })
            {
                string root = Environment.GetFolderPath(folder);
                if (root.Length > 0 && IsUnder(full, NormalizeForComparison(root)))
                    throw new IOException($"Refusing to write {path}: it's inside {root}, a system folder.");
            }
        }

        if (File.Exists(full) && !allowedExtensions.Any(ext => full.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            throw new IOException($"Refusing to overwrite {path}: only an existing {string.Join(" or ", allowedExtensions)} file is replaced.");

        ThrowIfRedirected(full);
    }

    private static bool IsUnder(string fullPath, string root) =>
        fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
        || fullPath.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>One spelling per location, for the folder comparison above: device prefixes
    /// (\\?\, \\.\, \??\) stripped - a device path that isn't then a plain drive or UNC path is
    /// refused - and 8.3 short names (C:\PROGRA~1) expanded on the part of the path that exists.</summary>
    private static string NormalizeForComparison(string path)
    {
        string p = path;
        bool devicePrefixed = false;
        foreach (string prefix in new[] { @"\\?\UNC\", @"\\.\UNC\", @"\??\UNC\" })
        {
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { p = @"\\" + p[prefix.Length..]; devicePrefixed = true; break; }
        }
        if (!devicePrefixed && (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal)))
        {
            p = p[4..];
            devicePrefixed = true;
        }
        if (devicePrefixed && !Path.IsPathFullyQualified(p))
            throw new IOException($"Refusing to write {path}: device paths aren't an output location.");

        string full = Path.GetFullPath(p);
        string existing = full;
        string rest = string.Empty;
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            string? parent = Path.GetDirectoryName(existing);
            if (parent is null) return Path.TrimEndingDirectorySeparator(full);
            rest = rest.Length == 0 ? Path.GetFileName(existing) : Path.Combine(Path.GetFileName(existing), rest);
            existing = parent;
        }
        string resolved = rest.Length == 0 ? LongPath(existing) : Path.Combine(LongPath(existing), rest);
        return Path.TrimEndingDirectorySeparator(resolved);
    }

    private static string LongPath(string path)
    {
        var buffer = new StringBuilder(512);
        uint length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder((int)length + 1);
            length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
        }
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferLength);

    /// <summary>The unattended scan runs as SYSTEM (UnattendedScanService) but writes under the
    /// registering user's own, user-writable %AppData% folder, and every other CLI output path can
    /// be supplied by a shortcut the user can edit unelevated - so a standard user could replace a
    /// folder on that path with a junction and have this process create directories and files
    /// wherever it points. Refuse to write through a reparse point on the path that a non-admin
    /// could have planted or could re-point: one owned by anyone but SYSTEM/Administrators/
    /// TrustedInstaller, or whose own or parent ACL lets anyone else delete, replace or
    /// re-permission it. An admin-made junction (e.g. C:\Users moved to another drive) passes; an
    /// unreadable ACL fails closed. As SYSTEM every reparse point counts; elevated as the user only
    /// junctions and symbolic links do, since a OneDrive/cloud-files placeholder is a user-owned
    /// reparse point that doesn't redirect anything. (A swap between this check and the write is
    /// still conceivable; this closes the plain redirect.)</summary>
    internal static void ThrowIfRedirected(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        bool isSystem = identity.IsSystem;
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && IsRedirecting(current, isSystem)
                && FindNonAdminControl(current) is { } reason)
                throw new IOException($"Refusing to write through a junction or symbolic link ({current}) - {reason}.");
            current = Path.GetDirectoryName(current);
        }
    }

    // LinkTarget is non-null for exactly the redirecting kinds: junctions and symbolic links.
    private static bool IsRedirecting(string path, bool isSystem)
    {
        if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return false;
        if (isSystem) return true;
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.LinkTarget is not null;
    }

    // Same trust model as UnattendedScanService.FindNonAdminWriteAccess (SYSTEM, Administrators,
    // TrustedInstaller), applied to a reparse point and the folder holding it.
    private static readonly HashSet<string> TrustedSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18",
        "S-1-5-32-544",
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
    };

    // Re-pointing a reparse point needs write access to it; replacing it needs delete on it or
    // delete-child on its parent; ACL/owner rights allow granting either.
    private const FileSystemRights ReparsePointRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.Delete | FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;
    private const FileSystemRights ParentRights =
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>Null when only trusted principals can change or replace the reparse point at
    /// `path`; otherwise why not.</summary>
    private static string? FindNonAdminControl(string path)
    {
        try
        {
            var sections = AccessControlSections.Access | AccessControlSections.Owner;
            FileSystemSecurity own = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(sections)
                : new FileInfo(path).GetAccessControl(sections);
            if (CheckAcl(own, path, ReparsePointRights) is { } problem) return problem;

            if (Path.GetDirectoryName(path) is { Length: > 0 } parent)
                return CheckAcl(new DirectoryInfo(parent).GetAccessControl(sections), parent, ParentRights);
            return null;
        }
        catch (Exception ex)
        {
            return $"its permissions couldn't be read ({ex.Message})";
        }
    }

    private static string? CheckAcl(FileSystemSecurity security, string path, FileSystemRights dangerous)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
            && !TrustedSids.Contains(owner.Value))
            return $"{path} is owned by {owner.Value}";
        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
            if ((rule.FileSystemRights & dangerous) == 0) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && TrustedSids.Contains(sid.Value)) continue;
            return $"{path} is writable by {rule.IdentityReference.Value}";
        }
        return null;
    }

    private static string ResolveScanOutputPath(string outputPath) =>
        outputPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? outputPath
            : Path.Combine(outputPath, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"), "findings.json");

    /// <summary>#996: `--collect &lt;path&gt;` - runs EvidenceBundleService.CollectAsync with a
    /// default full item selection (BuildCatalog(), the same "collect everything" default the
    /// Evidence Bundle panel's own button uses) and writes the resulting zip to `path`. Same
    /// one-shot ViewModel construction as ScanAsync above (EvidenceBundleService.CollectContext
    /// needs the same handful of ViewModels the rules engine does).</summary>
    public static async Task CollectAsync(string outputZipPath, bool scrub, bool quiet)
    {
        _ = quiet; // #996: this path already shows no UI/dialogs (progress is only ever reported via IProgress<string>, which this CLI caller simply doesn't observe).

        using var performance = new PerformanceViewModel();
        using var energyThermals = new EnergyThermalsViewModel(performance);
        var systemSpecs = new SystemSpecsViewModel();
        using var services = new ServicesViewModel();
        var processHistory = new ProcessHistoryService();
        using var leakWatch = new LeakWatchViewModel();
        using var processes = new ProcessesViewModel(processHistory, leakWatch);
        using var rulesEngine = new RulesEngineService(performance);

        await Task.Delay(2500);
        await WaitForSpecsAsync(systemSpecs);

        var ctx = new EvidenceBundleService.CollectContext(performance, processes, energyThermals, systemSpecs, services, rulesEngine);
        // Admin-only staging (not %TEMP%): elevated tools write here, and the scrub rewrites and
        // the zip read these files back.
        var staging = AdminDataPaths.CreatePrivateTempDirectory("CliCollect", out string? stagingError)
            ?? throw new IOException($"Couldn't create a protected staging folder: {stagingError}");
        try
        {
            // One level down, readable by this account only (see EvidenceBundleViewModel).
            string stagingDir = QuarantineService.CreateOwnerReadableDirectory(Path.Combine(staging.FullName, "bundle")).FullName;
            var items = EvidenceBundleService.BuildCatalog();
            var manifest = await EvidenceBundleService.CollectAsync(items, stagingDir, ctx, progress: null, CancellationToken.None);

            PiiScrubber? scrubber = null;
            if (scrub)
            {
                var ruleSet = ScrubRulesService.Load();
                string? ssid = await ScrubRulesService.TryGetCurrentSsidAsync();
                scrubber = PiiScrubber.Build(ruleSet, ssid);
                var scrubbableFiles = EvidenceBundleService.GetScrubbableFiles(items, manifest, stagingDir);
                var (scrubbedTextByPath, _, _) = EvidenceBundleService.PreviewScrub(scrubbableFiles, scrubber);
                // Fails closed: anything that couldn't be scrubbed is left out of the zip.
                EvidenceBundleService.ApplyScrub(manifest, stagingDir, scrubbableFiles, scrubbedTextByPath);
                manifest.WasScrubbed = true;
            }

            // Scrubbed bundle: the manifest and index.html go through the scrubber too.
            var shipped = scrubber is null ? manifest : EvidenceBundleService.ScrubbedCopy(manifest, scrubber);
            EvidenceBundleService.WriteManifest(shipped, stagingDir);

            var bag = RulesEngineService.BuildMetricBag(performance, energyThermals, systemSpecs, services, processes, out var unavailable);
            var result = rulesEngine.Evaluate(bag, unavailable);
            var findings = SummaryViewModel.SortIssues(new List<HealthIssue>(result.Findings), HealthFindingSortMode.Impact);
            var timeline = new List<TimelineEvent>();
            timeline.AddRange(TimelineService.GetReliabilityCrashEvents());
            timeline.AddRange(TimelineService.GetServiceFailureEvents());
            timeline.AddRange(TimelineService.GetWindowsUpdateEvents());
            timeline.AddRange(TimelineService.GetSoftwareInstallEvents());
            timeline.AddRange(ThermalEventLogService.ReadAll());
            timeline.AddRange(await TimelineService.GetDriverInstallEventsAsync());

            var html = EvidenceBundleService.BuildIndexHtml(shipped, findings, timeline.OrderByDescending(e => e.Timestamp).ToList(), systemSpecs, SummarySettingsService.Load().ReportTheme);
            File.WriteAllText(Path.Combine(stagingDir, "index.html"), scrubber is null ? html : scrubber.Scrub(html));

            ThrowIfUnsafeOutput(outputZipPath, ".zip");
            var dir = Path.GetDirectoryName(Path.GetFullPath(outputZipPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            EvidenceBundleService.CreateZip(stagingDir, outputZipPath, manifest);
        }
        finally
        {
            AdminDataPaths.TryDelete(staging); // best-effort - see EvidenceBundleViewModel.CleanupStaging
        }
    }

    private static async Task<string> ScrubTextAsync(string text)
    {
        var ruleSet = ScrubRulesService.Load();
        string? ssid = await ScrubRulesService.TryGetCurrentSsidAsync();
        var scrubber = PiiScrubber.Build(ruleSet, ssid);
        return scrubber.Scrub(text);
    }
}
