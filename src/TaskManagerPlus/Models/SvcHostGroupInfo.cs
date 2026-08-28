namespace TaskManagerPlus.Models;

/// <summary>#761: one svchost.exe host group (e.g. "netsvcs", "LocalService", "termsvcs") - the set
/// of Win32 services HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Svchost's own REG_MULTI_SZ
/// value lists for that group name, cross-referenced against which of those services are currently
/// running and whose own ImagePath actually carries `-k &lt;group&gt;` (group membership in the
/// Svchost key is eligibility, not a live mapping - see ServiceControlService.ReadSvchostGroups).
/// CPU/working-set/handle/thread totals are rolled up per *process* (a single -k group can be split
/// across more than one svchost.exe instance since Windows 10 1703's per-service svchost split -
/// see SvcHostSplitInfo), the same granularity the real Task Manager's own svchost rows show.</summary>
public sealed class SvcHostGroupInfo
{
    public string GroupName { get; init; } = string.Empty;
    public IReadOnlyList<string> ServiceNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RunningServiceNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> ProcessIds { get; init; } = Array.Empty<int>();

    public long TotalWorkingSetBytes { get; init; }
    public int TotalHandleCount { get; init; }
    public int TotalThreadCount { get; init; }
    public TimeSpan TotalCpuTime { get; init; }

    public int ServiceCount => ServiceNames.Count;
    public int RunningServiceCount => RunningServiceNames.Count;
    public int ProcessCount => ProcessIds.Count;

    public string ProcessIdsText => ProcessIds.Count == 0 ? "(not running)" : string.Join(", ", ProcessIds);
    public string CpuTimeText => TotalCpuTime.TotalHours >= 1
        ? $"{(int)TotalCpuTime.TotalHours}h {TotalCpuTime.Minutes}m"
        : $"{(int)TotalCpuTime.TotalMinutes}m {TotalCpuTime.Seconds}s";
}

/// <summary>#762: HKLM\SYSTEM\CurrentControlSet\Control\SvcHostSplitThresholdInKB and whether
/// per-service svchost splitting is active on this machine - Windows enables splitting by default
/// (each eligible service in its own svchost.exe instance rather than several sharing one) once
/// installed RAM crosses roughly 3.5GB, unless this value overrides the threshold. See
/// ServiceControlService.ReadSvcHostSplitInfo.</summary>
public sealed class SvcHostSplitInfo
{
    // Windows' documented built-in default threshold, ~3.5 GB expressed in KB.
    public const long DefaultThresholdKb = 3_670_016;

    public long? ConfiguredThresholdKb { get; init; }
    public long TotalRamKb { get; init; }

    public long EffectiveThresholdKb => ConfiguredThresholdKb ?? DefaultThresholdKb;
    public bool IsSplittingActive => TotalRamKb >= EffectiveThresholdKb;

    public string CaptionText
    {
        get
        {
            string thresholdText = ConfiguredThresholdKb is { } kb
                ? $"SvcHostSplitThresholdInKB is set to {kb:N0} KB (~{kb / 1024.0 / 1024.0:0.#} GB)"
                : $"SvcHostSplitThresholdInKB is not set - Windows' built-in default (~{DefaultThresholdKb / 1024.0 / 1024.0:0.#} GB) applies";

            if (TotalRamKb <= 0) return $"{thresholdText}. This machine's installed RAM couldn't be read.";

            string activeText = IsSplittingActive
                ? "per-service svchost splitting is ACTIVE on this machine (most eligible services get their own svchost.exe)."
                : "per-service svchost splitting is INACTIVE on this machine (eligible services share host processes).";
            return $"{thresholdText}. This machine has {TotalRamKb / 1024.0 / 1024.0:0.#} GB RAM, so {activeText}";
        }
    }
}
