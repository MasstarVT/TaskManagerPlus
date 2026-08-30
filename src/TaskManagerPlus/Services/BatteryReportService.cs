using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Xml.Linq;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #641/#643: the full battery-health report - `powercfg /batteryreport /xml` (far richer than
/// the single LibreHardwareMonitorLib "Degradation Level" sensor EnergyThermalsViewModel.Battery
/// already shows: design/full-charge capacity, cycle count, manufacturer/chemistry, plus the
/// report's own capacity-history and recent-usage tables), with a root\wmi + Win32_PortableBattery
/// WMI fallback for the identity/capacity fields alone when powercfg is blocked, missing, or its
/// XML doesn't parse into anything recognizable.
///
/// Both `powercfg /batteryreport`'s XML shape and, doubly so, its exact field-name/date-format
/// quirks are not a documented, versioned contract Microsoft publishes (the same caveat every
/// other powercfg text/XML parse in this app already carries - see PowerPlanService's remarks).
/// Parsing here is therefore adaptive: elements are matched by local name only (ignoring whatever
/// namespace, if any, a given Windows build's report declares), and any field this parser doesn't
/// recognize is left null/empty ("Unknown") rather than guessed - the same
/// degrade-never-fabricate stance BootPerformanceService's own adaptive event-field scan takes
/// for a different undocumented source.
/// </summary>
public static class BatteryReportService
{
    /// <summary>Runs `powercfg /batteryreport`, falling back to the WMI-only path when the
    /// powercfg path returns nothing recognizable. Returns a status note for the fallback case
    /// (worth surfacing - it means the capacity-history chart and recent-usage table will be
    /// empty) and an empty string when the primary path succeeded or nothing at all is available.</summary>
    public static async Task<(BatteryReportInfo? Report, string StatusText)> GetReportAsync()
    {
        var fromPowercfg = await RunPowercfgReportAsync();
        if (fromPowercfg is not null) return (fromPowercfg, string.Empty);

        var fromWmi = await Task.Run(ReadWmiFallback);
        if (fromWmi is not null)
        {
            return (fromWmi,
                "powercfg /batteryreport wasn't available (blocked, missing, or an unrecognized report format) - " +
                "showing cycle count/capacity from WMI instead. No capacity-history or recent-usage data from this source.");
        }

        return (null, "No battery report available from powercfg or WMI on this system.");
    }

    // ---- #641: powercfg /batteryreport ----------------------------------------------------

    private static async Task<BatteryReportInfo?> RunPowercfgReportAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "TaskManagerPlus");
        string tempFile = Path.Combine(tempDir, $"battery-report-{Guid.NewGuid():N}.xml");
        try
        {
            Directory.CreateDirectory(tempDir);
            await RunProcessAsync("powercfg.exe", $"/batteryreport /xml /output \"{tempFile}\"", 20000);
            if (!File.Exists(tempFile)) return null;

            string xml = await File.ReadAllTextAsync(tempFile);
            return ParseBatteryReportXml(xml);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* best-effort cleanup */ }
        }
    }

    private static BatteryReportInfo? ParseBatteryReportXml(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch { return null; }

        var battery = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Battery");
        if (battery is null) return null;

        double? designCapacity = ReadDouble(battery, "DesignCapacity");
        double? fullChargeCapacity = ReadDouble(battery, "FullChargeCapacity");
        int? cycleCount = ReadInt(battery, "CycleCount");
        string manufacturer = ReadString(battery, "Manufacturer") ?? string.Empty;
        string chemistry = ReadString(battery, "Chemistry") ?? string.Empty;
        string serial = ReadString(battery, "SerialNumber") ?? string.Empty;

        var reportInfo = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ReportInformation");
        DateTime? generatedAt = reportInfo is not null &&
            TryParseFlexibleDate(ReadString(reportInfo, "LocalTime") ?? string.Empty, out var gen)
            ? gen : null;

        var capacityHistory = new List<BatteryCapacityHistoryEntry>();
        foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName == "CapacityHistoryEntry"))
        {
            if (!TryParseFlexibleDate(ReadString(entry, "PeriodStartDate") ?? string.Empty, out var start)) continue;
            capacityHistory.Add(new BatteryCapacityHistoryEntry
            {
                PeriodStart = start,
                FullChargeCapacityMwh = ReadDouble(entry, "FullChargeCapacity"),
                DesignCapacityMwh = ReadDouble(entry, "DesignCapacity"),
            });
        }

        var recentUsage = new List<BatteryUsageHistoryEntry>();
        foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName is "UsageEntry" or "UsageHistoryEntry"))
        {
            string startRaw = ReadString(entry, "StartTime") ?? ReadString(entry, "PeriodStartDate") ?? string.Empty;
            if (!TryParseFlexibleDate(startRaw, out var start)) continue;
            recentUsage.Add(new BatteryUsageHistoryEntry
            {
                PeriodStart = start,
                State = ReadString(entry, "State") ?? string.Empty,
                PowerSource = ReadString(entry, "Source") ?? string.Empty,
                CapacityRemainingMwh = ReadDouble(entry, "Capacity"),
            });
        }

        bool foundAnything = designCapacity is not null || fullChargeCapacity is not null || cycleCount is not null ||
            manufacturer.Length > 0 || capacityHistory.Count > 0 || recentUsage.Count > 0;
        if (!foundAnything) return null; // nothing recognizable - let the WMI fallback try instead

        return new BatteryReportInfo
        {
            Source = "powercfg /batteryreport",
            Manufacturer = manufacturer,
            Chemistry = chemistry,
            SerialNumber = serial,
            DesignCapacityMwh = designCapacity,
            FullChargeCapacityMwh = fullChargeCapacity,
            CycleCount = cycleCount,
            ReportGeneratedAt = generatedAt,
            CapacityHistory = capacityHistory.OrderBy(e => e.PeriodStart).ToList(),
            RecentUsage = recentUsage.OrderByDescending(e => e.PeriodStart).Take(50).ToList(),
        };
    }

    private static string? ReadString(XElement parent, string localName)
    {
        var value = parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static double? ReadDouble(XElement parent, string localName)
    {
        var raw = ReadString(parent, localName);
        return raw is not null && double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static int? ReadInt(XElement parent, string localName)
    {
        var raw = ReadString(parent, localName);
        return raw is not null && int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>powercfg's XML date/time fields are a known-quirky part of this otherwise
    /// undocumented format: some Windows builds wrap the locale-formatted string in Unicode
    /// bidi-direction marks (U+200E/U+200F) around date separators, which plain DateTime.Parse
    /// rejects outright. Strips any non-printable "format" characters first, then tries
    /// current-culture parsing before falling back to invariant. #1057: current culture goes
    /// FIRST because these strings are locale-formatted by powercfg itself - trying invariant
    /// (MM/dd) first meant that on a dd/MM locale every date with day &lt;= 12 "succeeded" with
    /// month and day swapped, landing months out of order in the capacity-fade chart.</summary>
    internal static bool TryParseFlexibleDate(string raw, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var cleaned = new string(raw.Where(c =>
            !char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
        if (cleaned.Length == 0) return false;

        return DateTime.TryParse(cleaned, CultureInfo.CurrentCulture, DateTimeStyles.None, out result) ||
               DateTime.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    // ---- #643: root\wmi / Win32_PortableBattery fallback -----------------------------------

    /// <summary>Used only when the powercfg XML path above returned nothing recognizable. No
    /// capacity-history/recent-usage tables exist in WMI, so those two lists stay empty on this
    /// path - the #642 fade chart simply has nothing to plot, the same "not enough data yet"
    /// state every other history chart in this app already shows gracefully.</summary>
    public static BatteryReportInfo? ReadWmiFallback()
    {
        int? cycleCount = null;
        double? fullChargeCapacity = null;
        double? designCapacity = null;
        string manufacturer = string.Empty;
        string serial = string.Empty;
        string chemistry = string.Empty;

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CycleCount FROM BatteryCycleCount");
            foreach (ManagementObject mo in searcher.Get())
            {
                try { cycleCount = Convert.ToInt32(mo["CycleCount"]); } catch { /* leave Unknown */ }
                break;
            }
        }
        catch { /* class unavailable on this system - leave Unknown */ }

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
            foreach (ManagementObject mo in searcher.Get())
            {
                try { if (mo["FullChargedCapacity"] is { } fcc) fullChargeCapacity = Convert.ToDouble(fcc); } catch { /* leave Unknown */ }
                break;
            }
        }
        catch { /* class unavailable - leave Unknown */ }

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi",
                "SELECT DesignedCapacity, ManufactureName, SerialNumber FROM BatteryStaticData");
            foreach (ManagementObject mo in searcher.Get())
            {
                try { if (mo["DesignedCapacity"] is { } dc) designCapacity = Convert.ToDouble(dc); } catch { /* leave Unknown */ }
                try { manufacturer = (mo["ManufactureName"] as string ?? string.Empty).Trim(); } catch { /* leave empty */ }
                try { serial = (mo["SerialNumber"] as string ?? string.Empty).Trim(); } catch { /* leave empty */ }
                break;
            }
        }
        catch { /* class unavailable - leave Unknown */ }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Chemistry, DesignCapacity, Name FROM Win32_PortableBattery");
            foreach (ManagementObject mo in searcher.Get())
            {
                try { if (mo["Chemistry"] is { } chem) chemistry = MapChemistry(Convert.ToInt32(chem)); } catch { /* leave empty */ }
                if (designCapacity is null)
                {
                    try { if (mo["DesignCapacity"] is { } dc2 && Convert.ToDouble(dc2) > 0) designCapacity = Convert.ToDouble(dc2); }
                    catch { /* leave Unknown */ }
                }
                if (manufacturer.Length == 0)
                {
                    try { manufacturer = (mo["Name"] as string ?? string.Empty).Trim(); } catch { /* leave empty */ }
                }
                break;
            }
        }
        catch { /* class unavailable - leave Unknown */ }

        bool foundAnything = cycleCount is not null || fullChargeCapacity is not null || designCapacity is not null ||
            manufacturer.Length > 0 || chemistry.Length > 0;
        if (!foundAnything) return null; // no WMI battery classes reported anything at all

        return new BatteryReportInfo
        {
            Source = "WMI fallback",
            Manufacturer = manufacturer,
            Chemistry = chemistry,
            SerialNumber = serial,
            DesignCapacityMwh = designCapacity,
            FullChargeCapacityMwh = fullChargeCapacity,
            CycleCount = cycleCount,
        };
    }

    /// <summary>Win32_PortableBattery.Chemistry's documented 1-8 value set.</summary>
    private static string MapChemistry(int code) => code switch
    {
        1 => "Other",
        2 => "Unknown",
        3 => "Lead Acid",
        4 => "Nickel Cadmium",
        5 => "Nickel Metal Hydride",
        6 => "Lithium-ion",
        7 => "Zinc Air",
        8 => "Lithium Polymer",
        _ => string.Empty,
    };

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism.</summary>
    private static Task<(string Output, int? ExitCode)> RunProcessAsync(string exe, string args, int timeoutMs)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
}
