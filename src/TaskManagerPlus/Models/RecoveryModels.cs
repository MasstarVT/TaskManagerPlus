namespace TaskManagerPlus.Models;

/// <summary>
/// Round 21, items 96-100: "Recovery, escalation and safe operation" - the final chunk of the
/// crash/BSOD-forensics domain. Boot configuration (96/97) and System Restore (98) are read-only-
/// until-an-explicit-button data, guided repair (99) reports plain outcome text (the tuple-return
/// convention every other shelled-out-tool action in this domain already uses - see
/// DriverVerifierService/CrashDumpConfigService - is kept there rather than a model class), and the
/// support bundle (100) is a single pass/fail result. Only the two list-bound cards (the boot-flag
/// audit and the restore-point list) get real model classes here.
/// </summary>

/// <summary>Item 97: one bcdedit `{current}` boot entry flag, decoded into plain English - see
/// BootRecoveryService.ReadBootConfigAuditAsync. IsWarning flags the small, fixed set of values
/// that either weaken driver-signature enforcement (testsigning, nointegritychecks) or hide boot
/// failures (a non-default bootstatuspolicy) - "quick flag, not a verdict" per CLAUDE.md: any of
/// these can be a deliberate, legitimate developer/debugging setup, not necessarily a problem.</summary>
public sealed class BootConfigFlag
{
    public string Name { get; init; } = string.Empty;
    public string RawValue { get; init; } = string.Empty;
    public string PlainEnglish { get; init; } = string.Empty;
    public bool IsWarning { get; init; }
}

/// <summary>Items 96/97: `bcdedit /enum {current}` parsed once and shared by both items - item 96's
/// "reboot into Safe Mode" card reads CurrentlyInSafeMode/CurrentSafeModeText off this same result
/// to decide whether to offer "reboot into Safe Mode" or "revert Safe Mode boot", and item 97's
/// card renders Flags as the plain-English audit list.</summary>
public sealed class BootConfigAudit
{
    public bool ReadOk { get; init; }
    public string? ErrorText { get; init; }
    public List<BootConfigFlag> Flags { get; init; } = new();
    public string RawOutput { get; init; } = string.Empty;

    public bool HasWarnings => Flags.Any(f => f.IsWarning);

    /// <summary>Item 96: null when the safeboot value isn't set at all (normal boot); otherwise the
    /// raw bcdedit value ("minimal", "network", "dsrepair") - drives whether the Recovery card
    /// offers "reboot into Safe Mode" or "revert Safe Mode boot, currently configured".</summary>
    public string? CurrentSafebootValue => Flags.FirstOrDefault(f => f.Name == "safeboot")?.RawValue;
    public bool CurrentlyConfiguredForSafeMode => !string.IsNullOrEmpty(CurrentSafebootValue);
}

/// <summary>Item 98: one System Restore point, straight off the `SystemRestore` WMI class's own
/// instance properties (root\default) - the same class RestorePointService.TryCreate already
/// writes through. SequenceNumber is the restore point's own stable identifier (what rstrui.exe's
/// list shows, and what an `rstrui.exe /RunAsUser` style scripted rollback would target), not a
/// UI-only row index.</summary>
public sealed class RestorePointInfo
{
    public int SequenceNumber { get; init; }
    public DateTime CreationTime { get; init; }
    public string Description { get; init; } = string.Empty;
    public string RestorePointTypeText { get; init; } = string.Empty;
}

/// <summary>Item 98: the enumerated restore-point list plus a best-effort "is System Protection
/// even turned on" flag - see RestorePointService.ReadSystemProtectionStatus's own remarks on
/// exactly how tentative that flag is (there's no single documented "System Protection is on/off"
/// API; this combines the DisableSR policy value with a Win32_ShadowStorage association check, per
/// CLAUDE.md's "quick flag, not a verdict"). ProtectionEnabled null means genuinely unknown (the
/// WMI read itself failed), not "off" - never fabricated.</summary>
public sealed class SystemProtectionStatus
{
    public bool ReadOk { get; init; } = true;
    public string? ErrorText { get; init; }
    public bool? ProtectionEnabled { get; init; }
    public string ProtectionStatusText { get; init; } = "Unknown";
    public List<RestorePointInfo> RestorePoints { get; init; } = new();
}

/// <summary>Item 99: one Microsoft-Windows-MemoryDiagnostics-Results event (1201 = no problems
/// found, 1101 = hardware errors were detected) - read back after the user runs mdsched.exe (a
/// self-scheduling, self-restarting external tool this app only launches, never drives directly),
/// so the result appears on this tab instead of only in the toast Windows shows once, easy to miss,
/// right after the triggering reboot. See EventLogService.ReadMemoryDiagnosticsResults.</summary>
public sealed class MemoryDiagnosticResultInfo
{
    public DateTime TimeCreated { get; init; }
    public bool HadErrors { get; init; }
    public string ResultText { get; init; } = string.Empty;
}
