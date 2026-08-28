namespace TaskManagerPlus.Models;

/// <summary>
/// One parsed block from `bcdedit /enum all /v` or `bcdedit /enum firmware` output (#724) -
/// bcdedit's text output is the documented, stable contract for BCD data (see CLAUDE.md's
/// "prefer a known Windows tool/API over raw interop" convention - the BCD registry hive's binary
/// layout is not one this app touches directly), so this reads it adaptively as a generic
/// name/value bag rather than hardcoding which options a given entry type can carry (that set
/// differs by loader type/Windows version and isn't a published schema, the same "adaptive field
/// read" tradeoff BootPerformanceService's own event-log parsing already takes). Multi-valued
/// options (e.g. displayorder, which lists more than one identifier) keep every value, in order.
/// </summary>
public sealed class BcdEntry
{
    /// <summary>The block header bcdedit printed, e.g. "Windows Boot Manager", "Windows Boot
    /// Loader", "Firmware Boot Manager", "Firmware Application (101fffff)".</summary>
    public string Header { get; set; } = string.Empty;

    /// <summary>Raw identifier as bcdedit printed it - a well-known alias like "{bootmgr}"/
    /// "{current}"/"{fwbootmgr}"/"{default}" or a GUID in braces.</summary>
    public string Identifier { get; set; } = string.Empty;

    /// <summary>Every "name  value" pair this entry carries, in the order bcdedit printed them.</summary>
    public Dictionary<string, List<string>> Options { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string name) => Options.TryGetValue(name, out var v) && v.Count > 0 ? v[0] : null;
    public IReadOnlyList<string> GetAll(string name) => Options.TryGetValue(name, out var v) ? v : Array.Empty<string>();

    /// <summary>Flattened "name: value" lines for the read-only inspector tree (#724), in the
    /// order bcdedit printed them (Dictionary&lt;,&gt; enumeration order matches insertion order
    /// in practice for this app's .NET runtime, but this doesn't rely on that - it's cosmetic
    /// either way for a read-only debug view).</summary>
    public IEnumerable<string> DisplayLines => Options.SelectMany(kv => kv.Value.Select(v => $"{kv.Key}: {v}"));
}

/// <summary>
/// Everything read from one `bcdedit /enum all /v` + `bcdedit /enum firmware` pass (#724) -
/// shared by every #724-731 feature so bcdedit is shelled out to exactly once per Startup tab
/// refresh, not once per feature (see BcdInspectorService's remarks). Available is false when
/// bcdedit itself couldn't be run/parsed at all - every dependent feature degrades to
/// "unavailable" together in that case, never a guess.
/// </summary>
public sealed class BcdStore
{
    public bool Available { get; init; }
    public string? Error { get; init; }
    public List<BcdEntry> Entries { get; init; } = new();
    public List<BcdEntry> FirmwareEntries { get; init; } = new();

    public BcdEntry? WindowsBootManager => Entries.FirstOrDefault(e => e.Identifier.Equals("{bootmgr}", StringComparison.OrdinalIgnoreCase));

    /// <summary>The loader entry Windows actually booted this session - bcdedit's own /v output
    /// shows this literally as the alias "{current}" (bcdedit resolves it, not this app guessing
    /// which GUID is "current").</summary>
    public BcdEntry? CurrentEntry => Entries.FirstOrDefault(e => e.Identifier.Equals("{current}", StringComparison.OrdinalIgnoreCase));

    public BcdEntry? FirmwareBootManager => FirmwareEntries.FirstOrDefault(e => e.Identifier.Equals("{fwbootmgr}", StringComparison.OrdinalIgnoreCase));

    /// <summary>Every "Windows Boot Loader"-headed entry (every installed/multi-boot OS entry,
    /// resume-from-hibernate entries, etc.) - the boot-menu list (#729) resolves displayorder
    /// against this plus WindowsBootManager/FirmwareEntries.</summary>
    public List<BcdEntry> Loaders => Entries.Where(e => e.Header.Contains("Boot Loader", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Resolves an identifier (well-known alias or GUID) to a human-readable description -
    /// falls back to the raw identifier when nothing in this store names it (e.g. a stale firmware
    /// NVRAM entry with no matching "Firmware Application" block - see FirmwareBootOrderInfo's
    /// remarks on why that's flagged rather than hidden).</summary>
    public string DescribeIdentifier(string identifier)
    {
        var fromAll = Entries.FirstOrDefault(e => e.Identifier.Equals(identifier, StringComparison.OrdinalIgnoreCase));
        if (fromAll?.Get("description") is { } d1 && !string.IsNullOrWhiteSpace(d1)) return d1;
        var fromFw = FirmwareEntries.FirstOrDefault(e => e.Identifier.Equals(identifier, StringComparison.OrdinalIgnoreCase));
        if (fromFw?.Get("description") is { } d2 && !string.IsNullOrWhiteSpace(d2)) return d2;
        return identifier;
    }
}

/// <summary>
/// #725: one boot-mode/integrity flag found on the current loader entry - a "quick flag, not a
/// verdict" (see CLAUDE.md's cross-cutting conventions): testsigning/safeboot/debug flags are all
/// routinely turned on deliberately (a driver developer, a support technician troubleshooting a
/// boot failure), not necessarily evidence of tampering. ClearCommandArgs is the exact bcdedit
/// argument string (no "bcdedit" prefix) that clears just this one flag - shown verbatim in the
/// confirmation dialog before it's ever run, and run exactly as shown, nothing more.
/// </summary>
public sealed class BootModeFlag
{
    public string OptionName { get; init; } = string.Empty;
    public string RawValue { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string ClearCommandArgs { get; init; } = string.Empty;

    public string ClearCommandText => $"bcdedit {ClearCommandArgs}";
}

/// <summary>
/// #727: one "performance trap" BCD option found on the current loader entry - options that cap
/// or otherwise limit what hardware Windows will actually use, which read as a genuine hardware
/// problem (missing RAM, fewer cores than expected) unless someone happens to check the BCD.
/// ObservedEffect is composed against this machine's real CPU/RAM totals where the option's value
/// can be quantified (see BcdInspectorService.DetectPerformanceTrapOptions).
/// </summary>
public sealed class PerformanceTrapOption
{
    public string OptionName { get; init; } = string.Empty;
    public string RawValue { get; init; } = string.Empty;
    public string ObservedEffect { get; init; } = string.Empty;
}

/// <summary>
/// #728: bootstatuspolicy/recoveryenabled on the current loader entry - together they decide
/// whether Startup Repair can ever launch automatically after a failed boot. IgnoreAllFailures +
/// recoveryenabled No is the one combination that silently disables it entirely, which is worth
/// surfacing plainly since neither value alone looks alarming.
/// </summary>
public sealed class BootStatusPolicyInfo
{
    public string? BootStatusPolicy { get; init; }
    public string? RecoveryEnabled { get; init; }

    public bool DisablesStartupRepair =>
        (BootStatusPolicy?.Equals("IgnoreAllFailures", StringComparison.OrdinalIgnoreCase) == true)
        && (RecoveryEnabled?.Equals("No", StringComparison.OrdinalIgnoreCase) == true);

    public string PolicyText => string.IsNullOrWhiteSpace(BootStatusPolicy) ? "Not set (Windows default - display all failures)" : BootStatusPolicy!;
    public string RecoveryText => string.IsNullOrWhiteSpace(RecoveryEnabled) ? "Not set (Windows default - Yes)" : RecoveryEnabled!;

    public string SummaryText => DisablesStartupRepair
        ? "bootstatuspolicy is IgnoreAllFailures and recoveryenabled is No - Startup Repair will never launch automatically after a failed boot, even a serious one."
        : "Startup Repair can launch automatically after a failed boot, as expected.";
}

/// <summary>One entry in the resolved boot-menu display order (#729) - Identifier/Description
/// resolved via BcdStore.DescribeIdentifier so the menu reads with real OS names, not raw GUIDs.</summary>
public sealed class BootMenuEntryRef
{
    public string Identifier { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
}

/// <summary>#729: {bootmgr}'s timeout/displaybootmenu/default/displayorder, resolved to friendly
/// names - the boot menu a user actually sees at power-on (on a multi-boot machine) or would see
/// if displaybootmenu were on.</summary>
public sealed class BootMenuInfo
{
    public int? TimeoutSeconds { get; init; }
    public bool? DisplayBootMenu { get; init; }
    public string? DefaultIdentifier { get; init; }
    public string DefaultDescription { get; init; } = "Unknown";
    public List<BootMenuEntryRef> DisplayOrder { get; init; } = new();

    public string DisplayBootMenuText => DisplayBootMenu switch { true => "Yes", false => "No", null => "Unknown" };
    public string TimeoutText => TimeoutSeconds is { } t ? $"{t}s" : "Unknown";
}

/// <summary>One NVRAM firmware boot entry (#730) - LooksStale is a best-effort flag only (a blank
/// description is unusual and often leftover cruft from a removed drive/OS), never a confirmed
/// "this points at a removed device": bcdedit's firmware enumeration doesn't expose a decoded
/// UEFI device path this app could reliably cross-reference against a live drive letter, so this
/// intentionally lists rather than fabricates a stale/valid verdict per entry (see CLAUDE.md's
/// "degrade to Unknown/hidden - never fabricate" convention).</summary>
public sealed class FirmwareBootEntry
{
    public string Identifier { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool LooksStale { get; init; }
}

/// <summary>#730: {fwbootmgr}'s displayorder from `bcdedit /enum firmware` - flags when Windows
/// Boot Manager isn't first (a common cause of "another OS/network boot grabs the machine"), with
/// a ready-to-copy `bcdedit /set {fwbootmgr} displayorder ...` fix command that moves it to the
/// front without disturbing the rest of the order. Read-only by design (#730's spec) - this app
/// never runs firmware NVRAM writes itself, only shows the command.</summary>
public sealed class FirmwareBootOrderInfo
{
    public List<FirmwareBootEntry> DisplayOrder { get; init; } = new();
    public bool WindowsBootManagerFirst { get; init; }
    public string? SuggestedFixCommand { get; init; }
}

/// <summary>#731: one BCD export sitting under AppPaths.SettingsDirectory\BcdBackups - restore is
/// never automated (see BcdInspectorService.ExportBackupAsync's remarks), only the matching
/// `bcdedit /import` command is shown, in plain text, for the user to run themselves.</summary>
public sealed class BcdBackupEntry
{
    public string FilePath { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; }

    public string ImportCommandText => $"bcdedit /import \"{FilePath}\"";
}
