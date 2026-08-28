using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Pure, static "quick flag, not a verdict" analysis over the already-read memory module list and
/// a handful of other already-available signals (corrected ECC errors, the last Windows Memory
/// Diagnostic result, memory-related bugcheck history) - #443 (mismatched DIMMs), #444 (channel
/// population), #446 (ECC presence), #442 (XMP/rated-vs-running hint) and #451 (the single RAM
/// health rollup card). No I/O of its own; SystemSpecsService supplies every input, mirroring the
/// same "compute over already-read data" shape StabilityViewModel.ComputeStabilityIndex already
/// uses for its own single-number rollup.
/// </summary>
public static class MemoryDiagnosticsService
{
    // #444: best-effort channel-letter extraction from a module's DeviceLocator/BankLocator.
    // Vendor naming isn't standardized (unlike, say, SMBIOS type codes) - these three patterns
    // cover the large majority of consumer board conventions seen in practice ("CHANNEL A",
    // "DIMM_A1"/"DIMM A2", or a bare "A1"/"B2"); anything else is left unlabeled rather than
    // guessed, and is excluded from the channel-population check below.
    private static readonly Regex ChannelWordRegex = new(@"CHANNEL\s*[-_\s]?([A-D])\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DimmLetterRegex = new(@"DIMM[_\s-]?([A-D])\d*\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareLetterRegex = new(@"^([A-D])\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ExtractChannelLabel(string deviceLocator, string bankLocator)
    {
        foreach (var text in new[] { deviceLocator, bankLocator })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var m = ChannelWordRegex.Match(text);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(deviceLocator))
        {
            var m = DimmLetterRegex.Match(deviceLocator);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();

            m = BareLetterRegex.Match(deviceLocator.Trim());
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        }

        return string.Empty;
    }

    /// <summary>#443: flags every module whose value for a given attribute isn't the most common
    /// ("modal") value among modules that report one at all - part number, capacity, rated speed,
    /// rank count, manufacturer. On an exact tie (e.g. two modules at 8 GB, two at 16 GB) the
    /// first-encountered value wins the tie and the other pair is flagged - which pair "wins"
    /// is arbitrary, but a real difference always produces a flag either way, which is what
    /// matters for a "quick flag, not a verdict" check. Mutates IsMismatched/MismatchReason on the
    /// passed-in modules directly (see MemoryModuleInfo's remarks for why those two fields are
    /// settable rather than init-only).</summary>
    public static void DetectMismatches(IReadOnlyList<MemoryModuleInfo> modules)
    {
        foreach (var m in modules) { m.IsMismatched = false; m.MismatchReason = string.Empty; }
        if (modules.Count < 2) return;

        var capacityMinority = NonModalValues(modules.Where(m => m.CapacityBytes > 0).Select(m => m.CapacityBytes).ToList());
        var speedMinority = NonModalValues(modules.Where(m => m.SpeedMhz > 0).Select(m => m.SpeedMhz).ToList());
        var partMinority = NonModalValues(modules.Where(m => !string.IsNullOrWhiteSpace(m.PartNumber)).Select(m => m.PartNumber).ToList(), StringComparer.OrdinalIgnoreCase);
        var rankMinority = NonModalValues(modules.Where(m => m.RankCount.HasValue).Select(m => m.RankCount!.Value).ToList());
        var manufacturerMinority = NonModalValues(modules.Where(m => !string.IsNullOrWhiteSpace(m.Manufacturer)).Select(m => JedecManufacturerLookup.Resolve(m.Manufacturer)).ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var m in modules)
        {
            var reasons = new List<string>();
            if (m.CapacityBytes > 0 && capacityMinority.Contains(m.CapacityBytes)) reasons.Add("capacity");
            if (m.SpeedMhz > 0 && speedMinority.Contains(m.SpeedMhz)) reasons.Add("rated speed");
            if (!string.IsNullOrWhiteSpace(m.PartNumber) && partMinority.Contains(m.PartNumber)) reasons.Add("part number");
            if (m.RankCount.HasValue && rankMinority.Contains(m.RankCount.Value)) reasons.Add("rank");
            if (!string.IsNullOrWhiteSpace(m.Manufacturer) && manufacturerMinority.Contains(JedecManufacturerLookup.Resolve(m.Manufacturer))) reasons.Add("manufacturer");

            if (reasons.Count == 0) continue;
            m.IsMismatched = true;
            m.MismatchReason = $"Doesn't match the other module(s) in: {string.Join(", ", reasons)}";
        }
    }

    private static HashSet<T> NonModalValues<T>(IReadOnlyList<T> values, IEqualityComparer<T>? comparer = null) where T : notnull
    {
        comparer ??= EqualityComparer<T>.Default;
        var result = new HashSet<T>(comparer);
        var groups = values.GroupBy(v => v, comparer).ToList();
        if (groups.Count <= 1) return result;

        var modal = groups.OrderByDescending(g => g.Count()).First().Key;
        foreach (var g in groups)
            if (!comparer.Equals(g.Key, modal)) result.Add(g.Key);
        return result;
    }

    /// <summary>#444: describes how populated modules are spread across channels - a single-
    /// channel-only layout on a board with 2+ modules is flagged (running effectively single-
    /// channel on hardware that could take advantage of dual/quad-channel is a common, easy-to-miss
    /// performance loss), but only once there's more than one module to spread out at all - a
    /// genuinely one-DIMM system isn't a "problem", it's just what's installed.</summary>
    public static (string Text, bool Warning) CheckChannelPopulation(IReadOnlyList<MemoryModuleInfo> modules)
    {
        var withChannel = modules.Where(m => !string.IsNullOrEmpty(m.ChannelLabel)).ToList();
        if (withChannel.Count == 0)
            return ("Channel layout couldn't be determined from this board's slot naming.", false);

        var channels = withChannel.Select(m => m.ChannelLabel).Distinct().ToList();
        if (modules.Count >= 2 && channels.Count == 1)
        {
            return ($"All {withChannel.Count} populated module(s) report channel {channels[0]} - if this board supports more than one channel, spreading modules across channels (dual/quad-channel) usually gives a real memory-bandwidth improvement.", true);
        }

        var perChannelCounts = withChannel
            .GroupBy(m => m.ChannelLabel)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} in channel {g.Key}");
        return ($"Populated channels: {string.Join(", ", perChannelCounts)}.", false);
    }

    /// <summary>#446: combines the array-level Win32_PhysicalMemoryArray.MemoryErrorCorrection text
    /// with per-module SMBIOS width evidence (TotalWidthBits &gt; DataWidthBits) - two independent
    /// sources that normally agree; when they don't (rare, and usually means one of the two
    /// couldn't be read reliably on this board), this says so explicitly rather than picking one.</summary>
    public static (string Text, bool EccPresent) DescribeEcc(string arrayErrorCorrectionText, IReadOnlyList<MemoryModuleInfo> modules)
    {
        var withWidthData = modules.Where(m => m.HasEccWidth.HasValue).ToList();
        bool? moduleEcc = withWidthData.Count == 0 ? null : withWidthData.All(m => m.HasEccWidth == true) ? true
            : withWidthData.All(m => m.HasEccWidth == false) ? false : (bool?)null; // mixed - unusual, treated as uncertain

        bool arrayKnown = !string.IsNullOrWhiteSpace(arrayErrorCorrectionText) &&
            !arrayErrorCorrectionText.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
            !arrayErrorCorrectionText.Equals("Other", StringComparison.OrdinalIgnoreCase);
        bool? arrayEcc = !arrayKnown ? null : !arrayErrorCorrectionText.Equals("None", StringComparison.OrdinalIgnoreCase);

        if (arrayEcc is null && moduleEcc is null)
            return ("Unknown - neither the memory array nor individual modules report ECC capability details on this system.", false);

        if (arrayEcc.HasValue && moduleEcc.HasValue && arrayEcc.Value != moduleEcc.Value)
        {
            return ($"Uncertain - the memory array reports \"{arrayErrorCorrectionText}\" but per-module data width suggests {(moduleEcc.Value ? "ECC" : "no ECC")}; treat this as inconclusive rather than a confirmed answer.", moduleEcc.Value || arrayEcc.Value);
        }

        bool present = arrayEcc ?? moduleEcc!.Value;
        if (!present) return ("No ECC - this is standard (non-ECC) memory.", false);

        string detail = arrayKnown ? $"ECC present and active ({arrayErrorCorrectionText})." : "ECC present (per-module width data), array-level correction type not reported.";
        return (detail, true);
    }

    /// <summary>#442: extends the existing SpeedMhz-vs-ConfiguredSpeedMhz hint with the voltage
    /// figures SMBIOS adds - "XMP profile available: DDR5-6000 @ 1.35 V, currently running JEDEC
    /// DDR5-4800" instead of only "configured speed is lower than rated". This reports the
    /// module's own rated maximum speed/voltage vs. what firmware actually configured - it is not
    /// literally reading a named XMP/EXPO profile string (SMBIOS/WMI expose no such field; that
    /// would need direct SPD/SMBus access - see #441's own remarks on why that's LibreHardwareMonitorLib-
    /// dependent and frequently unsupported), so the wording says "may be available", never a
    /// certainty. Null when the module is already running at its rated speed (nothing to report).</summary>
    public static string? DescribeXmpHint(MemoryModuleInfo module)
    {
        if (module.SpeedMhz <= 0 || module.ConfiguredSpeedMhz <= 0 || module.ConfiguredSpeedMhz >= module.SpeedMhz)
            return null;

        string ddrPrefix = !string.IsNullOrWhiteSpace(module.MemoryType) && module.MemoryType != "Unknown"
            ? module.MemoryType + "-" : string.Empty;
        string ratedVoltage = module.MaxVoltageV is { } mv ? $" @ {mv:0.00} V" : string.Empty;
        string configuredVoltage = module.ConfiguredVoltageV is { } cv ? $" @ {cv:0.00} V" : string.Empty;
        return $"XMP/EXPO profile may be available: rated {ddrPrefix}{module.SpeedMhz:0}{ratedVoltage}, currently running {ddrPrefix}{module.ConfiguredSpeedMhz:0}{configuredVoltage} (JEDEC default).";
    }

    /// <summary>#451: single RAM health rollup - see RamHealthSummary's remarks for what this is
    /// (and explicitly isn't). Every input here was already read for another card on this same
    /// tab (or the Stability tab); this method does no I/O of its own.</summary>
    public static RamHealthSummary BuildRamHealth(
        IReadOnlyList<MemoryModuleInfo> modules,
        bool channelWarning,
        int correctedErrorCount,
        MemoryDiagnosticResultInfo? diagnosticResult,
        int memoryRelatedBugcheckCount)
    {
        var findings = new List<string>();
        bool warning = false;

        var mismatched = modules.Where(m => m.IsMismatched).ToList();
        if (mismatched.Count > 0)
        {
            findings.Add($"{mismatched.Count} of {modules.Count} module(s) don't match the others in the array — see the Memory modules list below for which module(s) and why.");
            warning = true;
        }

        if (channelWarning)
        {
            findings.Add("All populated modules appear to be on the same channel - see Channel layout below.");
            warning = true;
        }

        foreach (var m in modules)
        {
            if (DescribeXmpHint(m) is not null)
            {
                findings.Add($"{m.Location} is running below its rated speed - an XMP/EXPO profile may be available but not enabled.");
            }
        }

        if (correctedErrorCount > 0)
        {
            findings.Add($"{correctedErrorCount} corrected memory error(s) logged by Windows (WHEA) in the last 30 days - occasional corrected errors happen on healthy ECC systems, but a high or climbing count is worth a closer look.");
            warning = true;
        }

        if (diagnosticResult is { Passed: false })
        {
            findings.Add($"The last Windows Memory Diagnostic run ({diagnosticResult.TimeCreated:g}) reported errors.");
            warning = true;
        }

        if (memoryRelatedBugcheckCount > 0)
        {
            findings.Add($"{memoryRelatedBugcheckCount} memory-related bugcheck(s) found in the Stability tab's crash history.");
            warning = true;
        }

        if (findings.Count == 0)
            findings.Add("No mismatched modules, corrected errors, or diagnostic failures found.");

        return new RamHealthSummary
        {
            Verdict = warning ? "Worth a look — see findings below" : "No issues detected",
            IsWarning = warning,
            Findings = findings,
        };
    }
}
