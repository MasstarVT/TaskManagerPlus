using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Backs the GPU tab (#53-56): per-engine utilization and VRAM usage via the "GPU Engine"/"GPU
/// Adapter Memory" perf-counter categories (the same "GPU Engine" category Round 4's per-process
/// GPU column already reads via ProcessMonitorService.ReadGpuUsageByPid, just aggregated per-adapter
/// here instead of per-process), plus a one-time static read of installed adapter identity (driver
/// version/date, best-effort WDDM version) via Win32_VideoController and the display driver's
/// registry Class subkey.
///
/// Both perf-counter categories key their instances by a LUID (the 64-bit locally-unique adapter
/// identifier Windows assigns each GPU, split into two hex halves in the instance name) - there is
/// no public API mapping a LUID back to a Win32_VideoController row, so this only ever *pairs* live
/// LUID-keyed data with a static identity when the live LUID count matches the static adapter count
/// exactly (by far the common single-GPU case): each live group is then assigned the identity at the
/// same ordinal position. When the counts don't match (a hybrid laptop where the integrated GPU has
/// no live counter data until something actually renders on it, multiple LUIDs for one physical
/// adapter, ...) this deliberately does NOT guess a pairing - the live row falls back to a generic
/// "GPU N" label with its identity fields left blank, rather than risk showing one adapter's driver
/// info next to another adapter's live utilization. The "Installed adapters" identity list is always
/// shown in full regardless, so #55/#56 (driver info, multi-GPU list) are honestly answered even
/// when the live pairing above can't be.
/// </summary>
public sealed class GpuMonitorService : IDisposable
{
    private const string DisplayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";

    // #680: pid is now captured (group 1) alongside the LUID halves (groups 2/3) and engine type
    // (group 4) - the same instance-name parse this app already relied on for the per-adapter
    // engine breakdown, just also keeping the pid this once so it can be re-aggregated per-process
    // too (see ReadEngineUtilizationByLuid's remarks).
    private static readonly Regex EngineInstanceRegex = new(
        @"^pid_(\d+)_luid_(0x[0-9A-Fa-f]+)_(0x[0-9A-Fa-f]+)_phys_\d+_eng_\d+_engtype_(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MemoryInstanceRegex = new(
        @"^luid_(0x[0-9A-Fa-f]+)_(0x[0-9A-Fa-f]+)_phys_\d+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Previous-tick CounterSamples per engine instance - utilization is a busy-time rate, so each
    // value needs last tick's sample. One ReadCategory() call per tick replaces the old
    // PerformanceCounter-per-instance dictionaries: every raw NextValue() re-reads its ENTIRE
    // category, and "GPU Engine" routinely has 700+ instances, which made this sampler cost
    // ~500ms per tick (see ProcessPerfCounterService's remarks for the same fix and numbers).
    private readonly Dictionary<string, CounterSample> _engineSamples = new(StringComparer.OrdinalIgnoreCase);


    public IReadOnlyList<GpuAdapterIdentity> Adapters { get; }

    /// <summary>False when this system exposes neither Win32_VideoController rows nor the "GPU
    /// Engine" perf-counter category - a headless/basic-display-only VM being the realistic case -
    /// so the tab can show an "unavailable" state instead of a permanently-empty grid that looks
    /// broken.</summary>
    public bool IsAvailable { get; }

    /// <summary>#680: per-process GPU engine breakdown from the most recent <see cref="Sample"/>
    /// call, keyed by pid - each process' engines sorted descending by percent (so ".First()" is
    /// its top engine). Set as a side effect of Sample() rather than returned separately, the same
    /// "read once, expose via a property the caller reads afterward" shape this app already uses
    /// for a few other GPU-tab-only reads.</summary>
    public IReadOnlyDictionary<int, List<GpuEngineUsage>> LastPerProcessEngineUsage { get; private set; } =
        new Dictionary<int, List<GpuEngineUsage>>();

    public GpuMonitorService()
    {
        Adapters = ReadStaticAdapters();
        IsAvailable = Adapters.Count > 0 || CategoryExists("GPU Engine");
    }

    private static bool CategoryExists(string name)
    {
        try { return PerformanceCounterCategory.Exists(name); }
        catch { return false; }
    }

    public List<GpuAdapterSnapshot> Sample()
    {
        var (engineByLuid, engineByPid) = ReadEngineUtilizationByLuid();
        LastPerProcessEngineUsage = engineByPid.ToDictionary(
            kv => kv.Key,
            kv => kv.Value
                .Select(e => new GpuEngineUsage { EngineType = e.Key, Percent = Math.Round(Math.Clamp(e.Value, 0, 100), 1) })
                .OrderByDescending(e => e.Percent)
                .ToList());
        var (dedicatedByLuid, sharedByLuid, committedByLuid) = ReadAdapterMemoryByLuid();

        var liveLuids = engineByLuid.Keys.Union(dedicatedByLuid.Keys).Union(sharedByLuid.Keys)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool canPairOrdinal = Adapters.Count > 0 && Adapters.Count == liveLuids.Count;

        var result = new List<GpuAdapterSnapshot>();
        for (int i = 0; i < liveLuids.Count; i++)
        {
            var luid = liveLuids[i];
            engineByLuid.TryGetValue(luid, out var engines);
            dedicatedByLuid.TryGetValue(luid, out var dedicatedUsed);
            sharedByLuid.TryGetValue(luid, out var sharedUsed);
            committedByLuid.TryGetValue(luid, out var committedUsed);

            var identity = canPairOrdinal ? Adapters[i] : null;
            var engineList = (engines ?? new Dictionary<string, double>())
                .Select(kv => new GpuEngineUsage { EngineType = kv.Key, Percent = Math.Round(Math.Clamp(kv.Value, 0, 100), 1) })
                .OrderByDescending(e => e.Percent)
                .ToList();

            result.Add(new GpuAdapterSnapshot
            {
                Luid = luid,
                Name = identity?.Name ?? $"GPU {i + 1}",
                NameIsExact = identity is not null,
                IsIntegrated = identity?.IsIntegrated ?? false,
                Engines = engineList,
                TotalUtilizationPercent = engineList.Count == 0 ? 0 : engineList.Max(e => e.Percent),
                DedicatedVramUsedBytes = dedicatedUsed,
                DedicatedVramTotalBytes = identity?.AdapterRamBytes ?? 0,
                SharedVramUsedBytes = sharedUsed,
                TotalCommittedBytes = committedUsed,
                DriverVersion = identity?.DriverVersion ?? string.Empty,
                DriverDate = identity?.DriverDate ?? string.Empty,
                WddmVersion = identity?.WddmVersion ?? "Unknown",
            });
        }
        return result;
    }

    /// <summary>Sums "Utilization Percentage" per (LUID, engine type) AND, #680, per (pid, engine
    /// type) in the same single pass over every process/engine instance - a rate-style counter
    /// (PERF_100NSEC_TIMER), so a newly-seen instance is skipped for one tick rather than reported
    /// as a false 0, the same "prime before trusting a rate counter" rule
    /// ProcessMonitorService.ReadGpuUsageByPid already follows for the same category. The per-pid
    /// breakdown reuses this exact parsed instance-name data (the pid is now captured by
    /// EngineInstanceRegex alongside the LUID/engine-type groups it already captured) rather than a
    /// second counter enumeration - #680 explicitly calls for extending this aggregation, not
    /// re-parsing.</summary>
    private (Dictionary<string, Dictionary<string, double>> ByLuid, Dictionary<int, Dictionary<string, double>> ByPid) ReadEngineUtilizationByLuid()
    {
        var byLuid = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        var byPid = new Dictionary<int, Dictionary<string, double>>();
        try
        {
            // One ReadCategory per tick - see _engineSamples' declaration remarks for why this is
            // not a PerformanceCounter per instance.
            var category = new PerformanceCounterCategory("GPU Engine").ReadCategory();
            var utilization = category["Utilization Percentage"];
            if (utilization is null) return (byLuid, byPid);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (InstanceData instanceData in utilization.Values)
            {
                string instance = instanceData.InstanceName;
                seen.Add(instance);

                var sample = instanceData.Sample;
                bool havePrevious = _engineSamples.TryGetValue(instance, out var previous);
                _engineSamples[instance] = sample;
                // A rate's first-ever sample of an instance can't produce a value yet (the old
                // code burned a priming NextValue() for the same reason) - it reads next tick.
                if (!havePrevious) continue;

                var match = EngineInstanceRegex.Match(instance);
                if (!match.Success) continue;
                if (!int.TryParse(match.Groups[1].Value, out int pid)) continue;
                string luid = $"{match.Groups[2].Value}_{match.Groups[3].Value}";
                string engineType = SplitPascalCase(match.Groups[4].Value);

                double value = CounterSample.Calculate(previous, sample);

                if (!byLuid.TryGetValue(luid, out var byType))
                    byLuid[luid] = byType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                byType[engineType] = byType.TryGetValue(engineType, out var existing) ? existing + value : value;

                // pid 0 is the "Idle"/System pseudo-process GPU Engine reports for unattributed
                // usage - not a real process, so it's excluded from the per-process breakdown (it
                // would otherwise show up as a phantom top "process" using the GPU).
                if (pid <= 0) continue;
                if (!byPid.TryGetValue(pid, out var pidByType))
                    byPid[pid] = pidByType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                pidByType[engineType] = pidByType.TryGetValue(engineType, out var existingPid) ? existingPid + value : value;
            }

            foreach (var stale in _engineSamples.Keys.Where(k => !seen.Contains(k)).ToList())
                _engineSamples.Remove(stale);
        }
        catch
        {
            // "GPU Engine" category missing entirely - degrade to "no live utilization data".
        }
        return (byLuid, byPid);
    }

    /// <summary>Sums "Dedicated Usage"/"Shared Usage" per LUID - plain instantaneous byte gauges
    /// (not rates, unlike the engine counters above), so no priming is needed, the same treatment
    /// the Memory tab's Committed/Cache counters get.</summary>
    private (Dictionary<string, long> Dedicated, Dictionary<string, long> Shared, Dictionary<string, long> Committed) ReadAdapterMemoryByLuid()
    {
        var dedicated = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var shared = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var committed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // One ReadCategory serves all three gauges - same rationale as the engine counters
            // above. These are instantaneous byte values, so no previous-sample bookkeeping.
            var category = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory();
            var dedicatedData = category["Dedicated Usage"];
            var sharedData = category["Shared Usage"];
            // Older Windows builds may not expose "Total Committed" - degrade to 0 (Committed
            // hidden in the UI), same as the old per-counter creation failure did.
            var committedData = category["Total Committed"];
            if (dedicatedData is null && sharedData is null && committedData is null)
                return (dedicated, shared, committed);

            static void Accumulate(System.Diagnostics.InstanceDataCollection? data, Regex instanceRegex, Dictionary<string, long> into)
            {
                if (data is null) return;
                foreach (InstanceData instanceData in data.Values)
                {
                    var match = instanceRegex.Match(instanceData.InstanceName);
                    if (!match.Success) continue;
                    string luid = $"{match.Groups[1].Value}_{match.Groups[2].Value}";
                    long v = instanceData.Sample.RawValue;
                    into[luid] = into.TryGetValue(luid, out var existing) ? existing + v : v;
                }
            }

            Accumulate(dedicatedData, MemoryInstanceRegex, dedicated);
            Accumulate(sharedData, MemoryInstanceRegex, shared);
            Accumulate(committedData, MemoryInstanceRegex, committed);
        }
        catch
        {
            // "GPU Adapter Memory" category missing entirely - degrade to "no live VRAM data".
        }
        return (dedicated, shared, committed);
    }

    /// <summary>One-time static read (#55/#56) - Win32_VideoController for name/driver version/date,
    /// cross-referenced with the driver's own registry Class subkey for the corrected 64-bit VRAM
    /// figure (WMI's AdapterRAM field is 32-bit and misreports on cards with 4 GB+, the same issue
    /// SystemSpecsService.ReadVramFromRegistry works around for the System tab) and a best-effort
    /// WDDM version.</summary>
    private static List<GpuAdapterIdentity> ReadStaticAdapters()
    {
        var result = new List<GpuAdapterIdentity>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, DriverVersion, DriverDate FROM Win32_VideoController");
            foreach (ManagementObject mo in searcher.Get())
            {
                string name = (mo["Name"] as string ?? "Unknown GPU").Trim();
                if (name.Length == 0) continue;

                long adapterRam = 0;
                try { adapterRam = Convert.ToInt64(mo["AdapterRAM"] ?? 0L); } catch { /* leave 0 */ }

                string driverDate = string.Empty;
                if (mo["DriverDate"] is string wmiDate)
                {
                    try { driverDate = ManagementDateTimeConverter.ToDateTime(wmiDate).ToString("yyyy-MM-dd"); }
                    catch { /* leave blank */ }
                }

                var (registryVram, wddmVersion) = ReadRegistryAdapterInfo(name);

                // Heuristic, not a verified fact - Intel integrated GPUs are overwhelmingly
                // "Intel(R) ... Graphics"-named; Intel's discrete Arc line is explicitly excluded
                // since it isn't integrated. Same "quick flag, not a verdict" tier as this app's
                // other name-substring heuristics (VPN detection, gigabit-negotiated-down, ...).
                bool isIntegrated = name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Arc", StringComparison.OrdinalIgnoreCase);

                result.Add(new GpuAdapterIdentity
                {
                    Name = name,
                    IsIntegrated = isIntegrated,
                    AdapterRamBytes = registryVram ?? adapterRam,
                    DriverVersion = (mo["DriverVersion"] as string ?? string.Empty).Trim(),
                    DriverDate = driverDate,
                    WddmVersion = wddmVersion,
                });
            }
        }
        catch
        {
            // No video controller reported (headless server SKU, basic display driver only) -
            // empty list; IsAvailable falls back to whether the perf-counter category exists.
        }
        return result;
    }

    /// <summary>Walks the display driver's Class\{guid}\NNNN registry subkeys looking for the one
    /// whose DriverDesc matches the adapter name from WMI - the same lookup
    /// SystemSpecsService.ReadVramFromRegistry uses for the corrected VRAM figure - and also reads a
    /// "WddmVersion" REG_DWORD from that same subkey when present. That value's exact major/minor
    /// encoding isn't a documented Microsoft contract (community diagnostic tooling reads it the
    /// same way, e.g. 30 -&gt; "WDDM 3.0"), so a value outside the plausible 10-39 range degrades to
    /// "Unknown" rather than showing a nonsense version number.</summary>
    private static (long? Vram, string WddmVersion) ReadRegistryAdapterInfo(string gpuName)
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{DisplayClassGuid}");
            if (classKey is null) return (null, "Unknown");

            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                if (!uint.TryParse(subKeyName, out _)) continue; // adapter subkeys are "0000", "0001", ...

                using var sub = classKey.OpenSubKey(subKeyName);
                if (sub is null) continue;
                if (sub.GetValue("DriverDesc") is not string desc || !desc.Equals(gpuName, StringComparison.OrdinalIgnoreCase))
                    continue;

                long? vram = sub.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    int i => i,
                    _ => (long?)null,
                };

                string wddm = "Unknown";
                if (sub.GetValue("WddmVersion") is int raw && raw is >= 10 and < 40)
                    wddm = $"{raw / 10}.{raw % 10}";

                return (vram, wddm);
            }
        }
        catch
        {
            // fall back to the WMI-reported value / "Unknown"
        }
        return (null, "Unknown");
    }

    private static string SplitPascalCase(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            // No break after a digit: "3D" (engtype_3D) is one token, not "3 D".
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]) && !char.IsDigit(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public void Dispose() => _engineSamples.Clear();
}
