namespace TaskManagerPlus.Models;

/// <summary>
/// Round 17, item 61: one HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit\
/// &lt;exe&gt; configuration - makes Windows capture a dump (or launch a monitor process) when a
/// process disappears WITHOUT raising an error (a plain, silent Environment.Exit/ExitProcess call,
/// or a crash so severe WER itself never gets a chance to run) - the gap LocalDumps (item 42) and
/// WER itself don't cover, since both only ever trigger from an actual reported fault. Same shape
/// as WerReportModels.LocalDumpsConfig (null fields mean "not set", never a fabricated default) -
/// see Services/SilentProcessExitService.
/// </summary>
public sealed class SilentProcessExitConfig
{
    public string? TargetExecutable { get; init; }
    public bool Exists { get; init; }

    /// <summary>1 = report via LocalDumps-style dump capture (needs the LocalDumps values for the
    /// same executable to also be configured, item 42); 2 = launch MonitorProcess instead. Null
    /// means the ReportingMode value itself isn't set.</summary>
    public int? ReportingMode { get; init; }

    public string? LocalDumpFolder { get; init; }
    public string? MonitorProcess { get; init; }

    public string ReportingModeText => ReportingMode switch
    {
        1 => "1 — capture a dump (uses this executable's LocalDumps settings)",
        2 => "2 — launch a monitor process",
        null => "Not set (silent-exit monitoring disabled)",
        _ => $"{ReportingMode} (unrecognized value)",
    };
}

/// <summary>Round 17, item 62: one Image File Execution Options "Debugger" hijack - a
/// Debugger value under HKLM\...\Image File Execution Options\&lt;exe&gt; silently redirects every
/// launch of that executable through the named debugger instead, a classic malware-persistence
/// trick as well as a legitimate (if easy to forget about) crash-debugging aid. Windows ships none
/// of these by default, so any entry found here is inherently worth a look.</summary>
public sealed class ImageFileExecutionOptionsHijack
{
    public string ExecutableName { get; init; } = string.Empty;
    public string DebuggerPath { get; init; } = string.Empty;
}

/// <summary>
/// Round 17, item 62: HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug (postmortem/
/// just-in-time debugger) plus a scan of every Image File Execution Options subkey for a Debugger
/// value - covers both "is a broken/unexpected crash-handling config actually the reason crash
/// dialogs look wrong" and "is something using IFEO to hijack process launches". Read-only (no
/// write action - unlike LocalDumps/SilentProcessExit, changing the postmortem debugger away from
/// Windows' own WerFault.exe is a much more invasive, rarely-intentional change this app doesn't
/// offer a one-click button for). See Services/PostmortemDebuggerService.
/// </summary>
public sealed class PostmortemDebuggerInfo
{
    public string? Debugger { get; init; }
    public string? Auto { get; init; }

    /// <summary>Same two values under the Wow6432Node copy of the key (32-bit process postmortem
    /// debugging on a 64-bit OS) - null when the Wow6432Node key itself isn't present (a 32-bit
    /// build of Windows, or the key was never created).</summary>
    public string? Wow64Debugger { get; init; }
    public string? Wow64Auto { get; init; }

    public List<ImageFileExecutionOptionsHijack> ImageFileExecutionOptionsHijacks { get; init; } = new();

    /// <summary>True when the primary Debugger value doesn't look like Windows' own default
    /// (WerFault.exe or Visual Studio's vsjitdebugger.exe) or the Wow6432Node copy diverges the
    /// same way, or at least one IFEO Debugger hijack was found - "quick flag, not a verdict" per
    /// CLAUDE.md: a non-default postmortem debugger can be entirely legitimate (a developer's own
    /// JIT debugger setup), this just means "worth a manual check".</summary>
    public bool HasNonDefaultEntries { get; init; }
}
