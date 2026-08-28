using System.IO;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #479/#480/#484: parses `pnputil /enum-drivers` (via PnpUtilService.EnumDriversAsync - the shell-
/// out itself lives there per this app's "each pnputil verb is a PnpUtilService method" convention)
/// into the Devices &amp; Drivers tab's "Driver store" view, then layers on:
///   - #480 staleness: groups packages by (OriginalName, Provider) and flags every one that isn't
///     the newest in its group, plus a best-effort on-disk size per package so a "total reclaimable"
///     figure can be shown.
///   - #484 in-use mapping: cross-references each package's PublishedName against every present
///     device's own bound driver node (Control\Class\{guid}\NNNN\InfPath) - the same registry path
///     DriverInventoryService.ComputeMatchQuality already reads MatchingDeviceId from for #458, just
///     one value over. This is what makes #481's "refuse to delete anything still bound to a present
///     device" hard block possible.
/// </summary>
public static class DriverStoreService
{
    public sealed class Result
    {
        public List<DriverStorePackage> Packages { get; init; } = new();
        public long ReclaimableBytes { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public static Task<Result> ListAsync() => Task.Run(async () =>
    {
        var (success, output) = await PnpUtilService.EnumDriversAsync();
        if (!success && string.IsNullOrWhiteSpace(output))
            return new Result { ErrorMessage = "pnputil /enum-drivers failed to run." };

        var packages = ParseEnumDrivers(output);
        if (packages.Count == 0 && !success)
            return new Result { ErrorMessage = "Couldn't parse pnputil's driver store listing." };

        foreach (var p in packages)
            p.SizeBytes = ComputePackageSizeBytes(p.OriginalName, p.DriverVersionText);

        long reclaimable = MarkStaleAndComputeReclaimable(packages);

        return new Result { Packages = packages, ReclaimableBytes = reclaimable };
    });

    /// <summary>#484: mutates each package's IsInUse/InUseText in place against the given present-
    /// device set - kept as a separate step (rather than folded into ListAsync above) so the
    /// ViewModel can re-run it cheaply whenever the device tree is reloaded, without re-parsing
    /// pnputil's output again.</summary>
    public static void ApplyInUseInfo(IEnumerable<DriverStorePackage> packages, IEnumerable<PnpDeviceNode> presentDevices)
    {
        var byInf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in presentDevices)
        {
            string? infName = ReadBoundInfName(device.DeviceId);
            if (string.IsNullOrEmpty(infName)) continue;

            if (!byInf.TryGetValue(infName, out var names)) byInf[infName] = names = new List<string>();
            names.Add(device.Name);
        }

        foreach (var p in packages)
        {
            if (byInf.TryGetValue(p.PublishedName, out var deviceNames))
            {
                p.IsInUse = true;
                p.InUseText = "In use by: " + string.Join(", ", deviceNames.Distinct(StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                p.IsInUse = false;
                p.InUseText = "Not in use by any present device";
            }
        }
    }

    /// <summary>The same "device's own Driver value -> Control\Class\{guid}\NNNN key" hop
    /// DriverInventoryService.ComputeMatchQuality uses for #458, reading InfPath (the published
    /// oemNN.inf name that key's package was installed under) instead of MatchingDeviceId.</summary>
    internal static string? ReadBoundInfName(string deviceId)
    {
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{deviceId}");
            string? driverRef = enumKey?.GetValue("Driver") as string; // "{classguid}\NNNN"
            if (string.IsNullOrEmpty(driverRef)) return null;

            using var classKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driverRef}");
            return classKey?.GetValue("InfPath") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>#480: flags every package that isn't the newest in its (OriginalName, Provider)
    /// group - ordered by the parsed DriverDate first, falling back to a numeric-aware comparison
    /// of the raw version text when dates tie or weren't parseable. Returns the summed on-disk size
    /// of every flagged (stale) package with a known size.</summary>
    private static long MarkStaleAndComputeReclaimable(List<DriverStorePackage> packages)
    {
        long reclaimable = 0;
        var groups = packages.GroupBy(
            p => (Original: p.OriginalName.Trim(), Provider: p.Provider.Trim()),
            new OriginalProviderComparer());

        foreach (var group in groups)
        {
            var ordered = group
                .OrderByDescending(p => p.DriverDate ?? DateTime.MinValue)
                .ThenByDescending(p => p.DriverVersionText, VersionTextComparer.Instance)
                .ToList();
            if (ordered.Count < 2) continue;

            for (int i = 1; i < ordered.Count; i++)
            {
                ordered[i].IsStale = true;
                if (ordered[i].SizeBytes is { } sz) reclaimable += sz;
            }
        }
        return reclaimable;
    }

    private sealed class OriginalProviderComparer : IEqualityComparer<(string Original, string Provider)>
    {
        public bool Equals((string Original, string Provider) x, (string Original, string Provider) y) =>
            string.Equals(x.Original, y.Original, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Provider, y.Provider, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Original, string Provider) obj) =>
            HashCode.Combine(obj.Original.ToLowerInvariant(), obj.Provider.ToLowerInvariant());
    }

    /// <summary>Best-effort dotted-version-aware comparison of pnputil's raw "Driver Version" text
    /// (e.g. "06/21/2006 10.0.19041.1") - tries to compare the trailing dotted-number portion
    /// numerically (so "10.0.19041.10" sorts after "10.0.19041.9"), falling back to a plain
    /// ordinal string comparison when either side doesn't parse as one. A tie-breaker only - the
    /// primary sort in MarkStaleAndComputeReclaimable above is the parsed DriverDate.</summary>
    private sealed class VersionTextComparer : IComparer<string>
    {
        public static readonly VersionTextComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var vx = ExtractVersion(x);
            var vy = ExtractVersion(y);
            if (vx is not null && vy is not null) return vx.CompareTo(vy);
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static Version? ExtractVersion(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string candidate = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(text) ?? text;
            return Version.TryParse(candidate, out var v) ? v : null;
        }
    }

    /// <summary>#480: best-effort on-disk size for one package, found by matching OriginalName
    /// against %windir%\System32\DriverStore\FileRepository's own "&lt;original-name&gt;_&lt;arch&gt;_
    /// &lt;hash&gt;" folder-naming convention, then disambiguating between multiple candidate folders
    /// (a common case - that's exactly what #480 is flagging) by comparing each candidate's own
    /// copy of the .inf's "DriverVer" directive against the version text pnputil reported. Returns
    /// null (never a guessed value) when no folder could be uniquely identified.</summary>
    internal static long? ComputePackageSizeBytes(string originalName, string driverVersionText)
    {
        if (string.IsNullOrWhiteSpace(originalName)) return null;
        try
        {
            string repoRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "DriverStore", "FileRepository");
            if (!Directory.Exists(repoRoot)) return null;

            string prefix = Path.GetFileNameWithoutExtension(originalName);
            if (prefix.Length == 0) return null;

            var candidates = Directory.EnumerateDirectories(repoRoot, prefix + "*", SearchOption.TopDirectoryOnly).ToList();
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return DirectorySize(candidates[0]);

            // Multiple folders share this OriginalName's prefix (typically several versions of the
            // same package) - disambiguate by comparing each candidate's own .inf copy's DriverVer
            // line (format "mm/dd/yyyy,x.x.x.x") against pnputil's "mm/dd/yyyy x.x.x.x" text.
            string normalizedTarget = driverVersionText.Replace(" ", ",").Trim();
            foreach (var dir in candidates)
            {
                string infPath = Path.Combine(dir, originalName);
                if (!File.Exists(infPath)) continue;

                string? driverVerLine = null;
                try
                {
                    driverVerLine = File.ReadLines(infPath)
                        .FirstOrDefault(l => l.TrimStart().StartsWith("DriverVer", StringComparison.OrdinalIgnoreCase));
                }
                catch { /* unreadable .inf - skip this candidate */ }
                if (driverVerLine is null) continue;

                int eq = driverVerLine.IndexOf('=');
                if (eq < 0) continue;
                string value = driverVerLine[(eq + 1)..].Replace(" ", "").Trim();

                if (value.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    return DirectorySize(dir);
            }

            // Couldn't uniquely identify which of several candidate folders is this package's -
            // degrade to Unknown rather than guess (e.g. summing all of them, which would double-
            // count space genuinely reclaimable only once).
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static long DirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* skip an unreadable file */ }
            }
        }
        catch { /* leave whatever was summed so far */ }
        return total;
    }

    /// <summary>Parses `pnputil /enum-drivers`' block-of-"Key:   Value"-lines-per-package text
    /// output - the same hand-rolled, English-Windows-locale-assuming parsing convention every
    /// other shelled-out tool's output gets in this app (driverquery's CSV header lookup, powercfg's
    /// device-name lists, ...). A new package block starts at every "Published Name:" line.</summary>
    internal static List<DriverStorePackage> ParseEnumDrivers(string output)
    {
        var packages = new List<DriverStorePackage>();
        string? publishedName = null, originalName = null, provider = null, className = null, classGuid = null, driverVersionText = null, signerName = null;

        void Flush()
        {
            if (string.IsNullOrEmpty(publishedName)) return;

            DateTime? driverDate = null;
            if (driverVersionText is { Length: > 0 })
            {
                string dateToken = driverVersionText.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                if (DateTime.TryParse(dateToken, out var parsed)) driverDate = parsed;
            }

            packages.Add(new DriverStorePackage
            {
                PublishedName = publishedName,
                OriginalName = originalName ?? string.Empty,
                Provider = string.IsNullOrWhiteSpace(provider) ? "Unknown" : provider,
                ClassName = string.IsNullOrWhiteSpace(className) ? "Unknown" : className,
                ClassGuid = classGuid ?? string.Empty,
                DriverDate = driverDate,
                DriverVersionText = driverVersionText ?? string.Empty,
                SignerName = string.IsNullOrWhiteSpace(signerName) ? "Unknown" : signerName,
            });

            publishedName = originalName = provider = className = classGuid = driverVersionText = signerName = null;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (value.Length == 0) continue;

            if (key.Equals("Published Name", StringComparison.OrdinalIgnoreCase))
            {
                Flush(); // start of a new package block
                publishedName = value;
            }
            else if (key.Equals("Original Name", StringComparison.OrdinalIgnoreCase)) originalName = value;
            else if (key.Equals("Provider Name", StringComparison.OrdinalIgnoreCase)) provider = value;
            else if (key.Equals("Class Name", StringComparison.OrdinalIgnoreCase)) className = value;
            else if (key.Equals("Class GUID", StringComparison.OrdinalIgnoreCase) || key.Equals("Class Guid", StringComparison.OrdinalIgnoreCase)) classGuid = value;
            else if (key.Equals("Driver Version", StringComparison.OrdinalIgnoreCase)) driverVersionText = value;
            else if (key.Equals("Signer Name", StringComparison.OrdinalIgnoreCase)) signerName = value;
            // Other keys pnputil may emit (e.g. "Boot Critical", "Extension ID") aren't shown in
            // this view and are deliberately ignored rather than causing a parse failure.
        }
        Flush(); // the last block in the output has no following "Published Name:" to trigger it

        return packages.OrderBy(p => p.OriginalName, StringComparer.OrdinalIgnoreCase)
                       .ThenByDescending(p => p.DriverDate ?? DateTime.MinValue)
                       .ToList();
    }
}
