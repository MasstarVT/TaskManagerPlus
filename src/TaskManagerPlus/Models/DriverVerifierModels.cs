namespace TaskManagerPlus.Models;

/// <summary>
/// Round 19, item 81: parsed result of `verifier /query` (which drivers are being verified right
/// now, live) and `verifier /querysettings` (the persistent flags/drivers that will apply after
/// the next reboot - not necessarily the same as what's live today if Verifier was just changed).
/// Best-effort text parsing, the same tolerance CrashDumpConfigService's own powercfg/manage-bde
/// parsing already uses - verifier.exe's exact wording isn't a stable, versioned contract, so a
/// line this app doesn't recognize is simply not extracted, never guessed (CLAUDE.md's "degrade
/// to Unknown, never fabricate"). RawQueryOutput/RawSettingsOutput are always kept in full so the
/// UI can offer the untouched tool output alongside the best-effort summary.
/// </summary>
public sealed class DriverVerifierStatus
{
    public bool QuerySucceeded { get; init; }
    public string? ErrorText { get; init; }

    /// <summary>True when at least one driver is being verified right now - what item 82's warning
    /// banner keys off, since a persistent setting that hasn't taken effect yet (no reboot since it
    /// was set) isn't slowing anything down today.</summary>
    public bool IsRunning { get; init; }

    public List<string> VerifiedDriverNames { get; init; } = new();

    /// <summary>The persistent flags `verifier /querysettings` reports - what will apply after the
    /// next reboot, which can differ from what's live now.</summary>
    public uint? PersistentFlagsRaw { get; init; }
    public List<string> PersistentFlagsDescription { get; init; } = new();
    public List<string> PersistentDriverNames { get; init; } = new();

    public string RawQueryOutput { get; init; } = string.Empty;
    public string RawSettingsOutput { get; init; } = string.Empty;

    public string StatusSummaryText { get; init; } = "Unknown";
}

/// <summary>Item 83: one loaded, non-Microsoft kernel driver the guided wizard offers to select
/// for standard verification - built from Win32_SystemDriver (loaded driver services, a known WMI
/// class) cross-checked against SignatureCheckService's own embedded-Authenticode vendor read, the
/// same "known WMI class + this app's existing signature-check infra" combination
/// SystemSpecsService.ReadOutdatedDrivers already uses for a related purpose. FileName is what
/// verifier.exe's own /driver argument expects (the .sys module name), not the service key name.</summary>
public sealed class NonMicrosoftDriverCandidate
{
    public string FileName { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public string? Vendor { get; init; }
}

/// <summary>Item 87: one pool tag's allocation counters from a single
/// NtQuerySystemInformation(SystemPoolTagInformation) sample - see PoolTagMonitorService.</summary>
public sealed class PoolTagSample
{
    public string Tag { get; init; } = string.Empty;
    public long PagedAllocs { get; init; }
    public long PagedFrees { get; init; }
    public long PagedUsedBytes { get; init; }
    public long NonPagedAllocs { get; init; }
    public long NonPagedFrees { get; init; }
    public long NonPagedUsedBytes { get; init; }
}

/// <summary>One on-demand sampling pass (item 87) - TakenAt lets the UI show "as of ..." and lets
/// StabilityViewModel diff a later sample against whichever one it kept as the session's baseline.</summary>
public sealed class PoolTagSnapshot
{
    public DateTime TakenAt { get; init; }
    public List<PoolTagSample> Tags { get; init; } = new();
}

/// <summary>One display row in the "Kernel pool by tag" panel - the latest sample's bytes plus
/// growth since the session's first sample (0 when this row IS the baseline sample, or when the
/// tag is new since the baseline - in which case growth equals the current value in full). A
/// "quick flag, not a verdict" per CLAUDE.md: a tag whose nonpaged bytes only ever grow across
/// repeated samples is worth a manual look, not proof of a leak by itself.</summary>
public sealed class PoolTagRow
{
    public string Tag { get; init; } = string.Empty;
    public long NonPagedUsedBytes { get; init; }
    public long NonPagedGrowthBytes { get; init; }
    public long PagedUsedBytes { get; init; }
    public long PagedGrowthBytes { get; init; }
    public long NonPagedAllocs { get; init; }

    // Display-ready strings, computed once here rather than via a converter - the shared
    // BytesToReadableConverter treats <= 0 specially (rate vs. capacity formatting) in ways that
    // don't fit a growth delta, which is routinely zero or negative (freed more than was allocated
    // since the baseline) and still a meaningful, real value worth showing plainly.
    public string NonPagedUsedText => FormatPlain(NonPagedUsedBytes);
    public string PagedUsedText => FormatPlain(PagedUsedBytes);
    public string NonPagedGrowthText => FormatSigned(NonPagedGrowthBytes);
    public string PagedGrowthText => FormatSigned(PagedGrowthBytes);

    private static readonly string[] Units = { "B", "KB", "MB", "GB" };

    private static string FormatPlain(long bytes)
    {
        double v = Math.Abs((double)bytes);
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {Units[i]}";
    }

    private static string FormatSigned(long bytes)
    {
        if (bytes == 0) return "0 B";
        string sign = bytes > 0 ? "+" : "-";
        return sign + FormatPlain(bytes);
    }
}

/// <summary>Item 86: this app's own record of when the guided Verifier wizard (items 83/84) last
/// turned Driver Verifier on - Verifier itself keeps no "enabled since" timestamp anywhere this app
/// can read, so this is the only source for "Verifier has been enabled for N days". Cleared back to
/// null by a successful /reset (item 82) so a disabled-then-forgotten Verifier doesn't keep counting
/// up. Persisted the same shape as every other settings file in this app - see
/// VerifierEnableHistoryService and AppPaths.</summary>
public sealed class VerifierEnableHistory
{
    public DateTime? EnabledAtUtc { get; set; }

    /// <summary>How many days of continuous "enabled" before the nag banner/health-check entry
    /// starts showing - a small, user-adjustable number rather than a hardcoded one, since a long
    /// diagnostic session is sometimes genuinely intentional.</summary>
    public int NagAfterDays { get; set; } = 3;

    /// <summary>The other half of item 86's "nag after a configurable number of days or reboots" -
    /// a persistent (non-volatile) Verifier session only actually starts checking after a reboot,
    /// so "it's survived N reboots since being turned on" is an equally valid staleness signal,
    /// independent of how many days have simply passed.</summary>
    public int NagAfterReboots { get; set; } = 2;

    /// <summary>How many distinct boot sessions have been observed since EnabledAtUtc - incremented
    /// by VerifierEnableHistoryService.RecordBootObservationIfChanged whenever this app notices the
    /// machine's own approximate boot time (Environment.TickCount64-derived, no WMI needed) has
    /// moved on from the last one it saw.</summary>
    public int RebootsSinceEnabled { get; set; }

    /// <summary>The approximate boot time last observed - not shown anywhere itself, just the
    /// comparison point RecordBootObservationIfChanged uses to notice a new boot happened.</summary>
    public DateTime? LastSeenBootUtc { get; set; }

    public static VerifierEnableHistory Defaults => new();
}
