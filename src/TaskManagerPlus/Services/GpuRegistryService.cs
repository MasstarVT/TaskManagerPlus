using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #671/#672/#673: display-driver configuration that lives outside the perf-counter/WMI data
/// GpuMonitorService already reads - TDR (Timeout Detection and Recovery) tuning and Hardware-
/// accelerated GPU Scheduling state, both read-only DWORD values under
/// HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers, plus a best-effort driver-version history
/// via `pnputil /enum-drivers` (a known Windows tool, not raw driver-store interop - same
/// "shell out and parse text" tradeoff as VolumeDiagnosticsService/NetworkDiagnosticsService).
/// Every read degrades to null/empty on a denied key or missing tool - never a fabricated value.
/// </summary>
public static class GpuRegistryService
{
    private const string GraphicsDriversKeyPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    /// <summary>#671: TdrLevel/TdrDelay/TdrDdiDelay/TdrLimitCount/TdrLimitTime - read-only, each
    /// left null when the value REG_DWORD isn't present (Windows applies its own documented
    /// default in that case - see GpuTdrRegistrySettings' remarks).</summary>
    public static GpuTdrRegistrySettings ReadTdrSettings()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(GraphicsDriversKeyPath);
            if (key is null) return new GpuTdrRegistrySettings();

            return new GpuTdrRegistrySettings
            {
                TdrLevel = ReadDword(key, "TdrLevel"),
                TdrDelaySeconds = ReadDword(key, "TdrDelay"),
                TdrDdiDelaySeconds = ReadDword(key, "TdrDdiDelay"),
                TdrLimitCount = ReadDword(key, "TdrLimitCount"),
                TdrLimitTimeSeconds = ReadDword(key, "TdrLimitTime"),
            };
        }
        catch
        {
            // Key denied/missing - degrade to "all defaults, nothing to flag".
            return new GpuTdrRegistrySettings();
        }
    }

    /// <summary>#672: HwSchMode - the same registry key as the TDR settings above. Driver-capability
    /// is inferred from the best-effort WDDM major.minor figure GpuMonitorService already reads for
    /// the "Installed adapters" card (HAGS needs WDDM 2.7+); see GpuHagsInfo.DriverLikelySupportsHags
    /// for why this is a derived quick flag, not a verified capability query.</summary>
    public static GpuHagsInfo ReadHagsInfo(string? primaryAdapterWddmVersion)
    {
        int? raw = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(GraphicsDriversKeyPath);
            if (key is not null) raw = ReadDword(key, "HwSchMode");
        }
        catch
        {
            // Key denied/missing - degrade to "not configured".
        }

        bool? supportsHags = null;
        if (!string.IsNullOrEmpty(primaryAdapterWddmVersion) &&
            primaryAdapterWddmVersion != "Unknown" &&
            double.TryParse(primaryAdapterWddmVersion, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var wddm))
        {
            supportsHags = wddm >= 2.7;
        }

        return new GpuHagsInfo { HwSchModeRaw = raw, DriverLikelySupportsHags = supportsHags };
    }

    private static int? ReadDword(RegistryKey key, string name)
        => key.GetValue(name) switch
        {
            int i => i,
            uint u => unchecked((int)u),
            _ => null,
        };

    // #673: `pnputil /enum-drivers /class Display` lists every driver package currently staged in
    // the driver store for the Display class, each block separated by a blank line - including
    // superseded packages Windows hasn't cleaned up yet, which is what makes a version *history*
    // possible at all (there's no separate "install log" API; the driver store listing is the
    // closest real substitute). Restricted to blocks whose Provider Name names a real GPU vendor,
    // since this class also picks up unrelated virtual-display drivers (remote-desktop tools, VR
    // headset virtual monitors, ...) that would otherwise pollute the bucket list.
    private static readonly string[] GpuVendorHints = { "NVIDIA", "AMD", "Advanced Micro Devices", "Intel" };

    private static readonly Regex DriverVersionLineRegex = new(
        @"^\s*Driver Version:\s*(\d{2}/\d{2}/\d{4})\s+(\S+)", RegexOptions.Compiled);
    private static readonly Regex ProviderLineRegex = new(@"^\s*Provider Name:\s*(.+?)\s*$", RegexOptions.Compiled);

    /// <summary>#673: best-effort driver-version history - one entry per currently-staged Display-
    /// class driver package from a real GPU vendor, newest first. Returns an empty list (never
    /// throws) when pnputil isn't reachable, denies access, or the machine only has one driver
    /// package staged (the overwhelmingly common case after Windows Update's own driver-store
    /// cleanup) - a single-entry result is still meaningful (it tells the caller "every TDR
    /// happened under this one version"), just not a multi-version trend.</summary>
    public static List<(string Version, DateTime? PublishDate)> ReadDisplayDriverVersionHistory()
    {
        var result = new List<(string Version, DateTime? PublishDate)>();
        try
        {
            var psi = new ProcessStartInfo("pnputil.exe", "/enum-drivers /class Display")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return result;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10_000);

            string? currentProvider = null;
            foreach (var line in output.Split('\n'))
            {
                var providerMatch = ProviderLineRegex.Match(line);
                if (providerMatch.Success) { currentProvider = providerMatch.Groups[1].Value; continue; }

                var versionMatch = DriverVersionLineRegex.Match(line);
                if (!versionMatch.Success) continue;

                bool isGpuVendor = currentProvider is not null &&
                    GpuVendorHints.Any(h => currentProvider.Contains(h, StringComparison.OrdinalIgnoreCase));
                if (!isGpuVendor) continue;

                DateTime? publishDate = DateTime.TryParse(versionMatch.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d) ? d : null;
                result.Add((versionMatch.Groups[2].Value, publishDate));
            }
        }
        catch
        {
            // pnputil missing/denied/timed out - degrade to "no version history available".
        }
        return result.OrderByDescending(e => e.PublishDate ?? DateTime.MinValue).ToList();
    }
}
