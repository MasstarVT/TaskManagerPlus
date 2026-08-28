namespace TaskManagerPlus.Models;

/// <summary>#743: one Active Setup component (HKLM\SOFTWARE\Microsoft\Active Setup\Installed
/// Components\&lt;GUID&gt; and its Wow6432Node twin) - a StubPath command Windows runs once per
/// user, the first time that user signs in after the component's HKLM Version changes. Pending vs.
/// Complete is read by comparing that HKLM "should run" version against the per-user HKCU copy of
/// the same value, exactly what Windows itself checks before deciding to run the stub again - see
/// ActiveSetupService.
/// </summary>
public sealed class ActiveSetupComponent
{
    public string ComponentKeyName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string StubPath { get; init; } = string.Empty;
    public string HklmVersion { get; init; } = "Unknown";
    public string HkcuVersion { get; init; } = "(never run for this user)";
    public string State { get; init; } = "Unknown";
    public bool IsWow6432 { get; init; }

    public string ViewLabel => IsWow6432 ? "32-bit" : "64-bit";
}

/// <summary>#744: one Winlogon value this app compares against Windows' own defaults - a quick
/// flag, not a verdict (see CLAUDE.md's cross-cutting conventions): a nonstandard Shell/Userinit
/// is the classic persistence technique this check exists to surface, but Taskman/AppSetup/GinaDLL
/// being set at all (they're normally absent) is itself the flag for those three, not a specific
/// expected string. See WinlogonIntegrityService.
/// </summary>
public sealed class WinlogonCheckEntry
{
    public string Scope { get; init; } = string.Empty;
    public string ValueName { get; init; } = string.Empty;
    public string ExpectedText { get; init; } = string.Empty;
    public string ActualText { get; init; } = string.Empty;
    public bool IsMismatch { get; init; }
}

/// <summary>#745: one Image File Execution Options entry that actually changes how an executable
/// launches - a Debugger redirect and/or a SilentProcessExit\MonitorProcess hook. Quick flag, not
/// a verdict: a Debugger value is exactly how legitimate compatibility shims and real debuggers
/// (Visual Studio's "Attach to Process", WinDbg, ...) attach too. See
/// ImageFileExecutionOptionsService.
/// </summary>
public sealed class ImageFileExecutionOptionsEntry
{
    public string ExecutableName { get; init; } = string.Empty;
    public string? Debugger { get; init; }
    public string? GlobalFlagHex { get; init; }
    public string? MonitorProcess { get; init; }

    /// <summary>#745: whether a process by this name is currently running - gates the "View in
    /// Processes" cross-link (see StartupView.xaml.cs's ViewIfeoExeInProcesses_Click), the same
    /// cross-tab pattern #708/#723 already established elsewhere on this tab.</summary>
    public bool IsCurrentlyRunning { get; init; }

    public bool HasDebugger => !string.IsNullOrEmpty(Debugger);
    public string DebuggerText => Debugger ?? "(none)";
    public string GlobalFlagText => GlobalFlagHex ?? "(none)";
    public string MonitorProcessText => MonitorProcess ?? "(none)";
    public string ProcessNameForFilter => System.IO.Path.GetFileNameWithoutExtension(ExecutableName);
}

/// <summary>#746: one DLL/package this app found in a global-injection-capable location, with its
/// signature status (reusing SignatureCheckService). Quick flag, not a verdict - AppInit_DLLs and
/// the LSA security/authentication package lists have legitimate uses (accessibility tools,
/// third-party credential providers, ...); AppCertDlls and an unexpected-shape KnownDLLs entry are
/// flagged unconditionally since those two are far more rarely legitimate. See
/// DllInjectionAuditService.
/// </summary>
public sealed class DllInjectionEntry
{
    public string Category { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ResolvedPath { get; init; } = string.Empty;
    public string SignatureStatus { get; init; } = "Unknown";
    public bool IsFlagged { get; init; }
    public string Note { get; init; } = string.Empty;
}

/// <summary>#746: the whole global-DLL-injection audit result - AppInit_DLLs is read from both the
/// 64-bit and 32-bit registry views (they're independent lists), plus every entry gathered from
/// AppCertDlls/LSA security &amp; authentication packages/the KnownDLLs anomaly check. See
/// DllInjectionAuditService.Read.
/// </summary>
public sealed class DllInjectionAuditResult
{
    public bool AppInitEnabled64 { get; init; }
    public string AppInitDlls64 { get; init; } = string.Empty;
    public bool AppInitEnabled32 { get; init; }
    public string AppInitDlls32 { get; init; } = string.Empty;
    public List<DllInjectionEntry> Entries { get; init; } = new();

    public int FlaggedCount => Entries.Count(e => e.IsFlagged);
    public string AppInitEnabled64Text => AppInitEnabled64 ? "On" : "Off";
    public string AppInitEnabled32Text => AppInitEnabled32 ? "On" : "Off";
}

/// <summary>#747: one scheduled task with a &lt;BootTrigger&gt; and/or &lt;LogonTrigger&gt;, read
/// from one combined `schtasks /query /xml ONE` export rather than one XML fetch per task - see
/// ScheduledTaskService.ListBootAndLogonTriggeredAsync. Folded into the Startup tab's main grid as
/// first-class StartupItem rows (Source = StartupSource.ScheduledTaskTrigger).
/// </summary>
public sealed class ScheduledTaskTriggerInfo
{
    public string TaskName { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public bool HasBootTrigger { get; init; }
    public bool HasLogonTrigger { get; init; }
    public bool IsEnabled { get; init; }

    public string TriggerDescription => (HasBootTrigger, HasLogonTrigger) switch
    {
        (true, true) => "At boot and at logon",
        (true, false) => "At boot",
        (false, true) => "At logon",
        _ => "Unknown trigger",
    };
}
