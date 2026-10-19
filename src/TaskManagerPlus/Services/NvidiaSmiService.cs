using System.IO;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #676: shells out to nvidia-smi.exe (when present in System32 - i.e. an NVIDIA driver is
/// installed) for the one *authoritative* GPU throttle-reason and VRAM-ECC-error source this app
/// can reach without a vendor SDK - LibreHardwareMonitorLib exposes neither. Text-parses
/// `nvidia-smi -q -d PERFORMANCE,ECC,POWER`'s indented "Label : Value" report format, the same
/// "known tool, parse its text output" tradeoff CLAUDE.md documents for schtasks/sc/vssadmin/etc.
/// Hides entirely (IsAvailable false) on any non-NVIDIA system - never a fabricated/empty card.
///
/// nvidia-smi's report layout has drifted across driver generations (older drivers title the
/// throttle section "Clocks Throttle Reasons" with machine-readable keys like
/// "clocks_throttle_reason_sw_power_cap"; current drivers use a plain-English "Clocks Event
/// Reasons" section with an "Active"/"Not Active" value per line - confirmed on a live dev machine
/// running driver 610.88). This parser matches by line *shape* (a label ending in one of the known
/// throttle-reason names, with an Active/Not Active value) rather than a fixed section path, so it
/// tolerates that drift reasonably well; a layout it doesn't recognize just leaves the
/// corresponding flag false / count null rather than throwing.
/// </summary>
public static class NvidiaSmiService
{
    private static string ExePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");

    public static bool IsAvailable
    {
        get
        {
            try { return File.Exists(ExePath); }
            catch { return false; }
        }
    }

    private static readonly Regex GpuBlockHeaderRegex = new(@"^GPU\s+[0-9A-Fa-f:.]+", RegexOptions.Compiled);
    private static readonly Regex LeafLineRegex = new(@"^(?<indent>\s*)(?<label>[^:]+?)\s*:\s*(?<value>.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex ActiveValueRegex = new(@"^(Active|Not Active)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<NvidiaSmiGpuStatus> Query()
    {
        var result = new List<NvidiaSmiGpuStatus>();
        if (!IsAvailable) return result;

        try
        {
            var names = QueryGpuNames();
            string report = RunNvidiaSmi("-q -d PERFORMANCE,ECC,POWER");
            if (string.IsNullOrWhiteSpace(report)) return result;

            var blocks = SplitIntoGpuBlocks(report);
            for (int i = 0; i < blocks.Count; i++)
                result.Add(ParseBlock(blocks[i], i < names.Count ? names[i] : $"GPU {i}"));
        }
        catch
        {
            // A hiccup shelling out (driver reset mid-call, unexpected output, ...) shouldn't take
            // the GPU tab down - degrade to "no nvidia-smi data this tick".
        }
        return result;
    }

    private static List<string> QueryGpuNames()
    {
        var names = new List<string>();
        try
        {
            string output = RunNvidiaSmi("-L");
            foreach (var line in output.Split('\n'))
            {
                var m = Regex.Match(line, @"^GPU\s+\d+:\s*(.+?)\s*\(UUID", RegexOptions.IgnoreCase);
                if (m.Success) names.Add(m.Groups[1].Value);
            }
        }
        catch { /* fall back to generic "GPU N" labels */ }
        return names;
    }

    /// <summary>Shared runner with a real 10s timeout: the old synchronous ReadToEnd ran before
    /// the bounded wait, so an nvidia-smi wedged after a TDR hung the GPU tab's refresh (and its
    /// _isRefreshing guard) forever. A timed-out run returns empty - "no data this tick".</summary>
    private static string RunNvidiaSmi(string args)
        => ToolRunner.RunCaptured(ExePath, args, TimeSpan.FromSeconds(10)).Output;

    /// <summary>Lines before the first "GPU 00000000:01:00.0" header (the "NVSMI LOG" banner,
    /// timestamp, driver/CUDA versions, "Attached GPUs") belong to no GPU and are dropped - kept as
    /// a block they became a fake all-clear GPU 0 and pushed every real GPU's name one slot down.</summary>
    private static List<string> SplitIntoGpuBlocks(string report)
    {
        var blocks = new List<string>();
        List<string>? current = null;
        foreach (var line in report.Split('\n'))
        {
            if (GpuBlockHeaderRegex.IsMatch(line))
            {
                // Every header gets its block, even an empty one, so block i stays GPU i for the
                // `-L` name lookup.
                if (current is not null) blocks.Add(string.Join("\n", current));
                current = new List<string>();
                continue; // the header line itself carries no field data
            }
            current?.Add(line);
        }
        if (current is not null) blocks.Add(string.Join("\n", current));
        return blocks;
    }

    private static readonly (string Hint, Action<NvidiaSmiStatusBuilder> Apply)[] ThrottleHints =
    {
        ("SW Power Cap", b => b.SwPowerCap = true),
        ("HW Thermal Slowdown", b => b.HwThermalSlowdown = true),
        ("HW Power Brake", b => b.HwPowerBrake = true),
        ("Sync Boost", b => b.SyncBoost = true),
    };

    private static NvidiaSmiGpuStatus ParseBlock(string block, string gpuName)
    {
        var builder = new NvidiaSmiStatusBuilder { GpuName = gpuName };
        bool inVolatile = false;
        long correctableSum = 0, uncorrectableSum = 0;
        bool foundCorrectable = false, foundUncorrectable = false;
        long singleBitSum = 0, doubleBitSum = 0;
        bool foundSingleBit = false, foundDoubleBit = false;

        foreach (var rawLine in block.Split('\n'))
        {
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0) continue;

            // Section-header tracking (no colon on the line at all) for the ECC Volatile subsection.
            if (!trimmed.Contains(':'))
            {
                if (trimmed.Equals("Volatile", StringComparison.OrdinalIgnoreCase)) inVolatile = true;
                else if (trimmed.Equals("Aggregate", StringComparison.OrdinalIgnoreCase) ||
                         trimmed.Equals("Aggregate Uncorrectable SRAM Sources", StringComparison.OrdinalIgnoreCase))
                    inVolatile = false;
                continue;
            }

            var leaf = LeafLineRegex.Match(rawLine);
            if (!leaf.Success) continue;
            string label = leaf.Groups["label"].Value.Trim();
            string value = leaf.Groups["value"].Value.Trim();

            // ---- #676: explicit throttle-reason flags - matched by label hint + Active/Not
            // Active value shape (disambiguates from the near-identical-looking "Clocks Event
            // Reasons Counters" duration lines, e.g. "Sync Boost : 0 us"). ----
            if (ActiveValueRegex.IsMatch(value))
            {
                foreach (var (hint, apply) in ThrottleHints)
                {
                    if (label.Contains(hint, StringComparison.OrdinalIgnoreCase) &&
                        value.Equals("Active", StringComparison.OrdinalIgnoreCase))
                        apply(builder);
                }
                continue;
            }

            // ---- ECC volatile correctable/uncorrectable - summed across whichever sub-fields
            // this driver generation reports (SRAM/DRAM split on newer drivers, a flat pair on
            // older ones), "N/A" (no ECC memory) simply contributes nothing. ----
            if (inVolatile && TryParseLong(value, out long v))
            {
                if (label.Contains("Uncorrectable", StringComparison.OrdinalIgnoreCase))
                { uncorrectableSum += v; foundUncorrectable = true; }
                else if (label.Contains("Correctable", StringComparison.OrdinalIgnoreCase))
                { correctableSum += v; foundCorrectable = true; }
            }

            // ---- Retired pages / remapped rows - best-effort, present only on ECC-capable
            // (workstation/datacenter) cards; N/A on a consumer card, which is expected. ----
            if (label.Contains("Single Bit ECC", StringComparison.OrdinalIgnoreCase) && TryParseLong(value, out long sb))
            { singleBitSum += sb; foundSingleBit = true; }
            else if (label.Contains("Double Bit ECC", StringComparison.OrdinalIgnoreCase) && TryParseLong(value, out long db))
            { doubleBitSum += db; foundDoubleBit = true; }
            else if (label.Contains("Remapped Rows", StringComparison.OrdinalIgnoreCase) && TryParseLong(value, out long rr))
            { builder.RemappedRows = (builder.RemappedRows ?? 0) + rr; }
        }

        builder.EccVolatileCorrectable = foundCorrectable ? correctableSum : null;
        builder.EccVolatileUncorrectable = foundUncorrectable ? uncorrectableSum : null;
        builder.RetiredPagesSingleBit = foundSingleBit ? singleBitSum : null;
        builder.RetiredPagesDoubleBit = foundDoubleBit ? doubleBitSum : null;
        return builder.Build();
    }

    private static bool TryParseLong(string value, out long result)
    {
        // nvidia-smi reports "N/A" for anything not applicable to this card (e.g. no ECC memory) -
        // that must not parse as 0, so this rejects non-numeric text outright rather than letting
        // long.TryParse's own leniency near-silently accept something unexpected.
        value = value.Trim();
        if (value.Equals("N/A", StringComparison.OrdinalIgnoreCase)) { result = 0; return false; }
        return long.TryParse(value, out result);
    }

    /// <summary>Plain mutable builder for NvidiaSmiGpuStatus's otherwise-init-only properties -
    /// the parser above fills these in incrementally while walking the block line by line.</summary>
    private sealed class NvidiaSmiStatusBuilder
    {
        public string GpuName = string.Empty;
        public bool SwPowerCap;
        public bool HwThermalSlowdown;
        public bool HwPowerBrake;
        public bool SyncBoost;
        public long? EccVolatileCorrectable;
        public long? EccVolatileUncorrectable;
        public long? RetiredPagesSingleBit;
        public long? RetiredPagesDoubleBit;
        public long? RemappedRows;

        public NvidiaSmiGpuStatus Build() => new()
        {
            GpuName = GpuName,
            SwPowerCap = SwPowerCap,
            HwThermalSlowdown = HwThermalSlowdown,
            HwPowerBrake = HwPowerBrake,
            SyncBoost = SyncBoost,
            EccVolatileCorrectable = EccVolatileCorrectable,
            EccVolatileUncorrectable = EccVolatileUncorrectable,
            RetiredPagesSingleBit = RetiredPagesSingleBit,
            RetiredPagesDoubleBit = RetiredPagesDoubleBit,
            RemappedRows = RemappedRows,
        };
    }
}
