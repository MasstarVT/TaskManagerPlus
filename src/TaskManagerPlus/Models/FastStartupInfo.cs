using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>
/// #734: Fast Startup (HiberbootEnabled) state, plus the reconciliation of the three clocks that
/// disagree once Fast Startup is on - Environment.TickCount64 (resets on every hybrid "shutdown +
/// power on" cycle, since the kernel session really does end and resume from a hiberfile),
/// Win32_OperatingSystem.LastBootUpTime (WMI's own opinion of "when did this session start" -
/// tracks TickCount64 closely in practice, shown alongside it for transparency rather than
/// silently trusted to always agree), and the last Kernel-Boot event 27 with boot type 0 (a real,
/// full cold boot - the one clock that can lag behind by days or weeks while a user keeps using
/// Fast Startup's hybrid shutdown instead of "Restart"). See FastStartupService.ReadUptimeInfo.
/// </summary>
public sealed class FastStartupInfo
{
    /// <summary>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power\HiberbootEnabled -
    /// null when the value couldn't be read (degrade to Unknown, never assume a default).</summary>
    public bool? HiberbootEnabled { get; init; }

    public TimeSpan TickCountUptime { get; init; }
    public DateTime? LastBootUpTimeWmi { get; init; }
    public DateTime? LastFullBootTime { get; init; }
    public TimeSpan? SinceLastFullRestart { get; init; }

    public bool IsFastStartupEnabled => HiberbootEnabled == true;

    /// <summary>#734: "Uptime 3h — but 41 days since your last full restart" - only worth saying
    /// once Fast Startup is on and the two figures meaningfully disagree (a plain full restart's
    /// tick-count uptime and its last-full-restart time are the same thing, nothing to call out).
    /// currentUptime is passed in live (rather than using TickCountUptime, captured once at load
    /// time) so the footer status bar can keep ticking every second alongside
    /// PerformanceViewModel.Uptime without this whole snapshot needing to be re-read.</summary>
    public string UptimeReconciliationText(TimeSpan currentUptime)
    {
        string upText = FormatSpan(currentUptime);
        if (!IsFastStartupEnabled || SinceLastFullRestart is not { } sinceFull)
            return $"Uptime {upText}";
        if (Math.Abs((sinceFull - currentUptime).TotalMinutes) < 5)
            return $"Uptime {upText}";
        return $"Uptime {upText} — but {FormatSpan(sinceFull)} since your last full restart";
    }

    public int? DaysSinceFullRestart => SinceLastFullRestart is { } s ? (int)s.TotalDays : null;

    public string HiberbootEnabledText => HiberbootEnabled switch { true => "On", false => "Off", null => "Unknown" };
    public string LastFullBootText => LastFullBootTime is { } t ? t.ToString("g") : "Unknown (no full-boot record found in the last 30 days)";
    public string LastBootUpTimeWmiText => LastBootUpTimeWmi is { } t ? t.ToString("g") : "Unknown";

    internal static string FormatSpan(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{(int)span.TotalMinutes}m";
    }
}

/// <summary>#735: persisted "dismiss this prompt for 7 days" state for the "you haven't fully
/// restarted in N days" card - same small-JSON-under-AppPaths.SettingsDirectory shape as every
/// other persisted setting in this app (see FastStartupPromptSettingsService).</summary>
public sealed class FastStartupPromptSettings
{
    public DateTime? DismissedUntilUtc { get; set; }
}

/// <summary>#737: one documented consequence of leaving Fast Startup on - informational only
/// ("quick flag, not a verdict", see CLAUDE.md's cross-cutting conventions), shown as a plain
/// explanation list rather than a single scary banner.</summary>
public sealed class FastStartupSideEffect
{
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

/// <summary>#736: one sleep state from `powercfg /a`'s "available"/"not available" report, with
/// the reason text powercfg itself gives for an unavailable one (not re-derived - shown verbatim,
/// same "adaptive read of the tool's own text" tradeoff BcdInspectorService's bcdedit parse
/// takes).</summary>
public sealed class SleepStateInfo
{
    public string Name { get; init; } = string.Empty;
    public bool Available { get; init; }
    public string? UnavailableReason { get; init; }
}

/// <summary>#736: hibernation file inventory - HiberFileSizeBytes is the real, measured
/// C:\hiberfil.sys size on disk (powercfg has no read-only query for the configured percentage,
/// only the /hibernate /size &lt;n&gt; setter - see FastStartupService.ReadHiberFileInfo's
/// remarks), compared against installed RAM to show the same "percentage of RAM" figure Windows'
/// own hibernation settings describe.</summary>
public sealed class HiberFileInfo
{
    public bool FileExists { get; init; }
    public long HiberFileSizeBytes { get; init; }
    public long TotalRamBytes { get; init; }

    public double? PercentOfRam => TotalRamBytes > 0 && FileExists ? (double)HiberFileSizeBytes / TotalRamBytes * 100 : null;

    public string SizeText => FileExists
        ? $"{Formatting.FormatBytes(HiberFileSizeBytes)}{(PercentOfRam is { } p ? $" (~{p:0.#}% of installed RAM)" : string.Empty)}"
        : "No hiberfil.sys found - hibernation is off.";
}
