using System.Diagnostics;
using System.Globalization;

namespace TaskManagerPlus.Services;

/// <summary>One driver package staged in the Windows Driver Store (#556), parsed from one
/// `pnputil /enum-drivers` block. <see cref="IsCurrentlyInstalled"/> is set by the caller once it's
/// cross-referenced against the adapter's own <c>AdapterDriverInfo</c> - never guessed here.</summary>
public sealed class StagedDriverPackage
{
    public string PublishedName { get; init; } = string.Empty; // e.g. "oem12.inf" - the Driver Store's own copy name
    public string OriginalName { get; init; } = string.Empty; // e.g. "netwtw10.inf" - the vendor's original file name
    public string ProviderName { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public string DriverVersion { get; init; } = string.Empty;
    public DateTime? DriverDate { get; init; }
    public bool IsCurrentlyInstalled { get; set; }
}

/// <summary>
/// Item #556: "available driver versions for the NIC" - runs `pnputil /enum-drivers` (the standard
/// tool for listing everything staged in the Driver Store, per CLAUDE.md's "known tool over raw
/// interop" convention) and matches each Net-class package against the adapter's own driver record
/// so the user can see whether an older or newer package is already staged on the machine to roll
/// back to - NOT a claim that a newer version is available online anywhere (this app makes no
/// network lookup for that, same honesty NetworkDiagnosticsService.ReadAdapterDriverInfo's own
/// LooksOld flag already documents).
///
/// pnputil's plain `/enum-drivers` listing doesn't include each package's compatible hardware IDs
/// (that needs parsing the actual INF file's [Models] section, a much larger undertaking), so the
/// match to "this adapter's hardware" is necessarily best-effort: Class GUID must match the Net
/// class, then published/original INF file name is compared against
/// <see cref="AdapterDriverInfo.InfName"/> (an exact match reliably identifies the currently-active
/// package), falling back to a Provider-vs-Manufacturer substring match for everything else. This is
/// the same "no exact join key, so match on the closest available field" tradeoff
/// UsbPowerService.ReadUsbSelectiveSuspend already accepts for its own best-effort join.
/// </summary>
public static class AdapterDriverStoreService
{
    private const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const int TimeoutMs = 20000;

    /// <summary>Every Driver Store package whose Class GUID is the Net device class, regardless of
    /// which (if any) currently-installed adapter it matches - callers cross-reference against a
    /// specific <see cref="AdapterDriverInfo"/> via <see cref="MarkInstalled"/>.</summary>
    public static async Task<List<StagedDriverPackage>> ReadNetDriverPackagesAsync()
    {
        var packages = new List<StagedDriverPackage>();
        string output;
        try
        {
            var psi = new ProcessStartInfo("pnputil.exe", "/enum-drivers")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return packages;

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return packages;
            }
            output = (await outputTask) + (await errorTask);
        }
        catch
        {
            return packages;
        }

        return Parse(output);
    }

    /// <summary>Marks (and returns, sorted so the match is obvious) whichever packages best match
    /// the given adapter - exact INF-name match first, falling back to a Provider/Manufacturer
    /// substring match. Doesn't mutate the shared list's ordering across different adapters, since
    /// each call operates on its own copy.</summary>
    public static List<StagedDriverPackage> MatchToAdapter(IEnumerable<StagedDriverPackage> allNetPackages, AdapterDriverInfo adapter)
    {
        var copy = allNetPackages.Select(p => new StagedDriverPackage
        {
            PublishedName = p.PublishedName,
            OriginalName = p.OriginalName,
            ProviderName = p.ProviderName,
            ClassName = p.ClassName,
            DriverVersion = p.DriverVersion,
            DriverDate = p.DriverDate,
        }).ToList();

        bool anyInfMatch = false;
        if (!string.IsNullOrWhiteSpace(adapter.InfName))
        {
            foreach (var p in copy)
            {
                if (p.PublishedName.Equals(adapter.InfName, StringComparison.OrdinalIgnoreCase) ||
                    p.OriginalName.Equals(adapter.InfName, StringComparison.OrdinalIgnoreCase))
                {
                    p.IsCurrentlyInstalled = true;
                    anyInfMatch = true;
                }
            }
        }

        // Only fall back to the fuzzier provider/manufacturer match when the exact INF name didn't
        // find anything - otherwise a same-vendor package that isn't actually installed would get
        // mislabeled "currently installed" alongside the real one.
        if (!anyInfMatch && !string.IsNullOrWhiteSpace(adapter.Manufacturer))
        {
            foreach (var p in copy)
            {
                if (p.ProviderName.Contains(adapter.Manufacturer, StringComparison.OrdinalIgnoreCase) ||
                    adapter.Manufacturer.Contains(p.ProviderName, StringComparison.OrdinalIgnoreCase))
                    p.IsCurrentlyInstalled = true;
            }
        }

        return copy
            .Where(p => p.ClassName.Contains("Network", StringComparison.OrdinalIgnoreCase) || p.IsCurrentlyInstalled)
            .OrderByDescending(p => p.IsCurrentlyInstalled)
            .ThenByDescending(p => p.DriverDate)
            .ToList();
    }

    private static List<StagedDriverPackage> Parse(string output)
    {
        var packages = new List<StagedDriverPackage>();
        var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void FlushBlock()
        {
            if (block.Count == 0) return;
            if (block.TryGetValue("Class GUID", out var guid) && guid.Equals(NetClassGuid, StringComparison.OrdinalIgnoreCase))
            {
                var (date, version) = SplitDriverVersion(block.GetValueOrDefault("Driver Version", string.Empty));
                packages.Add(new StagedDriverPackage
                {
                    PublishedName = block.GetValueOrDefault("Published Name", string.Empty),
                    OriginalName = block.GetValueOrDefault("Original Name", string.Empty),
                    ProviderName = block.GetValueOrDefault("Provider Name", string.Empty),
                    ClassName = block.GetValueOrDefault("Class Name", string.Empty),
                    DriverVersion = version,
                    DriverDate = date,
                });
            }
            block.Clear();
        }

        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) { FlushBlock(); continue; }

            int idx = line.IndexOf(':');
            if (idx < 0) continue;
            string key = line[..idx].Trim();
            string value = line[(idx + 1)..].Trim();
            if (key.Length == 0) continue;
            block[key] = value;
        }
        FlushBlock();

        return packages;
    }

    /// <summary>pnputil prints "Driver Version" as a date and a version number space-separated
    /// (e.g. "11/17/2021 12.19.1.5") - split defensively since that shape isn't a documented
    /// contract, just pnputil's observed output.</summary>
    private static (DateTime? Date, string Version) SplitDriverVersion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, string.Empty);
        int spaceIdx = raw.IndexOf(' ');
        if (spaceIdx < 0) return (null, raw.Trim());

        string datePart = raw[..spaceIdx].Trim();
        string versionPart = raw[(spaceIdx + 1)..].Trim();
        // #1060: the date comes from the INF's own DriverVer directive, which the INF format fixes
        // as MM/dd/yyyy regardless of locale - machine-formatted tool output, so InvariantCulture
        // per CLAUDE.md's parsing rule (a culture-less TryParse read d/M-locale dates wrong for
        // any day <= 12, feeding a wrong month into the driver-date sort).
        DateTime? date = DateTime.TryParse(datePart, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        return (date, versionPart.Length == 0 ? raw.Trim() : versionPart);
    }
}
