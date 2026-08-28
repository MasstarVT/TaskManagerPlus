using System.Diagnostics;
using System.Management;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 20, #895: preinstalled/OEM software inventory - combines three sources (the Uninstall
/// registry keys, `Get-AppxPackage`, and #834's existing `Get-AppxProvisionedPackage -Online`
/// read via AutorunsService.GetProvisionedAppxPackages - reused, not re-implemented) into one
/// tiered list. Tiering is a SIMPLE keyword-matching heuristic, not a perfect classifier - the
/// item's own text calls this out as an expected approximation, and TierLabel/BloatwareTier are
/// deliberately coarse for exactly that reason.
/// </summary>
public static class BloatwareInventoryService
{
    // Publishers/name-fragments treated as "an OEM name" for the OemUtility/OemUpdaterTelemetry
    // tiers below - the common Windows-PC manufacturers, not an exhaustive list.
    private static readonly string[] OemNames = { "Dell", "HP", "Hewlett-Packard", "Lenovo", "ASUS", "Asus", "Acer", "MSI", "Micro-Star", "Samsung", "Toshiba", "Gigabyte", "Razer" };
    private static readonly string[] OemUtilityKeywords = { "utility", "assist", "support", "center", "companion", "hub", "manager" };
    private static readonly string[] OemTelemetryKeywords = { "telemetry", "diagnostics", "update" };
    private static readonly string[] TrialwareKeywords = { "trial", "mcafee", "norton", "wildtangent" };
    private static readonly string[] DriverAdjacentKeywords = { "chipset", "audio driver", "realtek", "nvidia", "intel graphics", "hotkey", "power management", "geforce experience", "radeon software" };

    // AppX package-family-name prefixes treated as "Microsoft first-party, not Store bloat" -
    // small allowlist per the item's own text, not exhaustive.
    private static readonly string[] MicrosoftAppxPrefixes = { "Microsoft.", "Windows.", "MicrosoftWindows." };

    public static List<BloatwareEntry> Scan()
    {
        string manufacturer = ReadSystemManufacturer();
        var result = new List<BloatwareEntry>();

        result.AddRange(ReadUninstallRegistryEntries().Select(e => Classify(e, manufacturer)));
        result.AddRange(ReadInstalledAppxPackages().Select(e => Classify(e, manufacturer)));
        result.AddRange(ReadProvisionedAppxPackages().Select(e => Classify(e, manufacturer)));

        return result
            .GroupBy(e => $"{e.Source}|{e.Name}", StringComparer.OrdinalIgnoreCase) // same name from the same source (e.g. HKLM+Wow6432Node) is a dup; the same name from two different sources is shown twice on purpose - it IS installed twice
            .Select(g => g.First())
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ReadSystemManufacturer()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in searcher.Get())
                return (mo["Manufacturer"] as string ?? string.Empty).Trim();
        }
        catch { /* Unknown - the OEM-name keyword match below just won't fire */ }
        return string.Empty;
    }

    /// <summary>(a): HKLM + HKLM\Wow6432Node + HKCU Uninstall keys - the same per-app registry
    /// shape SystemSpecsService.ReadRecentlyInstalledSoftware already reads for its own "recently
    /// installed" list, extended here to the FULL inventory (no 6-month/Microsoft-publisher/Top-20
    /// filtering, since this item wants everything, tiered rather than trimmed).</summary>
    private static List<BloatwareEntry> ReadUninstallRegistryEntries()
    {
        var results = new List<BloatwareEntry>();
        (RegistryKey Hive, string Path)[] roots =
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, path) in roots)
        {
            try
            {
                using var uninstallKey = hive.OpenSubKey(path);
                if (uninstallKey is null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = uninstallKey.OpenSubKey(subKeyName);
                        if (sub is null) continue;

                        string name = (sub.GetValue("DisplayName") as string ?? string.Empty).Trim();
                        if (name.Length == 0) continue;
                        if (sub.GetValue("SystemComponent") is int sc && sc == 1) continue;

                        DateTime? installDate = null;
                        string dateRaw = (sub.GetValue("InstallDate") as string ?? string.Empty).Trim();
                        if (dateRaw.Length == 8 && DateTime.TryParseExact(dateRaw, "yyyyMMdd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var parsed))
                            installDate = parsed;

                        long? sizeKb = sub.GetValue("EstimatedSize") is int size ? size : null;

                        results.Add(new BloatwareEntry
                        {
                            Name = name,
                            Publisher = (sub.GetValue("Publisher") as string ?? string.Empty).Trim(),
                            InstallDate = installDate,
                            EstimatedSizeKb = sizeKb,
                            UninstallString = (sub.GetValue("UninstallString") as string ?? string.Empty).Trim(),
                            Source = BloatwareSource.UninstallRegistry,
                        });
                    }
                    catch { /* one malformed subkey shouldn't stop the rest */ }
                }
            }
            catch { /* hive/path unavailable */ }
        }

        return results.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    /// <summary>(b): Get-AppxPackage - installed Store apps for the current user. No existing
    /// read of this exact cmdlet anywhere in this app (only the -Provisioned variant, reused
    /// separately below) - a new PowerShell call, the same shelled-out CSV-parse shape
    /// AutorunsService.GetProvisionedAppxPackages already uses.</summary>
    private static List<BloatwareEntry> ReadInstalledAppxPackages()
    {
        var result = new List<BloatwareEntry>();
        try
        {
            string output = RunCapturedSync("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-AppxPackage | Select-Object Name,PackageFullName,Publisher,InstallDate | ConvertTo-Csv -NoTypeInformation\"",
                TimeSpan.FromSeconds(25));
            if (string.IsNullOrWhiteSpace(output)) return result;

            var lines = output.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count < 2) return result;

            foreach (var line in lines.Skip(1))
            {
                var fields = ParseCsvLine(line);
                if (fields.Count == 0 || string.IsNullOrWhiteSpace(fields[0])) continue;

                DateTime? installDate = null;
                if (fields.Count > 3 && DateTime.TryParse(fields[3], out var parsed)) installDate = parsed;

                result.Add(new BloatwareEntry
                {
                    Name = fields[0],
                    UninstallString = fields.Count > 1 ? fields[1] : string.Empty, // PackageFullName - Remove-AppxPackage's argument, shown for reference only
                    Publisher = fields.Count > 2 ? fields[2] : string.Empty,
                    InstallDate = installDate,
                    Source = BloatwareSource.AppxPackage,
                });
            }
        }
        catch
        {
            // PowerShell/AppX provider unavailable/failed/timed out - contribute nothing.
        }
        return result;
    }

    private static List<BloatwareEntry> ReadProvisionedAppxPackages()
        => AutorunsService.GetProvisionedAppxPackages()
            .Select(p => new BloatwareEntry { Name = p.DisplayName, UninstallString = p.PackageName, Source = BloatwareSource.AppxProvisionedPackage })
            .ToList();

    private static BloatwareEntry Classify(BloatwareEntry entry, string manufacturer)
    {
        string haystack = $"{entry.Name} {entry.Publisher}".ToLowerInvariant();
        bool mentionsOem = (manufacturer.Length > 0 && haystack.Contains(manufacturer.ToLowerInvariant()))
            || OemNames.Any(o => haystack.Contains(o.ToLowerInvariant()));

        BloatwareTier tier;
        if (DriverAdjacentKeywords.Any(k => haystack.Contains(k)))
            tier = BloatwareTier.DriverAdjacentDoNotRemove;
        else if (TrialwareKeywords.Any(k => haystack.Contains(k)))
            tier = BloatwareTier.Trialware;
        else if (mentionsOem && OemTelemetryKeywords.Any(k => haystack.Contains(k)))
            tier = BloatwareTier.OemUpdaterTelemetry;
        else if (mentionsOem && OemUtilityKeywords.Any(k => haystack.Contains(k)))
            tier = BloatwareTier.OemUtility;
        else if (entry.Source is BloatwareSource.AppxPackage or BloatwareSource.AppxProvisionedPackage
                 && !MicrosoftAppxPrefixes.Any(p => entry.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                                                      || entry.UninstallString.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            tier = BloatwareTier.StoreBloat;
        else
            tier = BloatwareTier.Unclassified;

        return new BloatwareEntry
        {
            Name = entry.Name,
            Publisher = entry.Publisher,
            InstallDate = entry.InstallDate,
            EstimatedSizeKb = entry.EstimatedSizeKb,
            UninstallString = entry.UninstallString,
            Source = entry.Source,
            Tier = tier,
        };
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }

    private static string RunCapturedSync(string exe, string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { proc.Kill(); } catch { /* best-effort */ }
            return string.Empty;
        }

        return outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult();
    }
}
