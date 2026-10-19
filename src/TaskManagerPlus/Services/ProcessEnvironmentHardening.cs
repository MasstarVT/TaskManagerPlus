using System.Collections;
using System.IO;
using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>
/// Cleans this process's environment once at startup, before any tool is started. An elevated
/// process still starts with the logged-on user's environment block, and HKCU\Environment is
/// writable without elevation - so every variable below is something an unelevated program can set
/// to steer what the elevated children of this app load or where they write:
/// - PSModulePath: PowerShell auto-loads a command's module from the first folder listed (the
///   user's Documents\WindowsPowerShell\Modules by default) - a planted module runs as admin.
/// - TEMP/TMP: DISM stages and runs DismHost from %TEMP%, WPR stages there, and Path.GetTempPath
///   callers follow it. Pointed at the admin-only AdminDataPaths "Temp" area when it can be set up.
/// - _NT_* / DBGHELP_*: debugger symbol/extension paths (cdb/WinDbg load DLLs from them).
/// - COR_* / CORECLR_* / COMPlus_* / DOTNET_*: profiler injection and runtime switches for any
///   .NET child (powershell.exe is one).
/// - ProgramFiles*, CommonProgramFiles*, SystemRoot, windir, ComSpec, PATHEXT, ProgramData: reset
///   to the machine's own values, so a "%ProgramFiles%" expansion can't point into a user folder.
/// The user's original TEMP is kept in <see cref="UserTempPath"/> for the code that measures or
/// flags it (reclaimable space, "runs from Temp" heuristics). Unelevated runs (the asInvoker test
/// build) still get everything except the TEMP move, which needs an admin-only folder.
/// </summary>
public static class ProcessEnvironmentHardening
{
    /// <summary>The logged-on user's own temp folder as it was at startup (always the user's,
    /// regardless of where this process's TEMP now points).</summary>
    public static string UserTempPath { get; private set; } = Path.GetTempPath();

    private static readonly string[] StrippedPrefixes =
    {
        "_NT_", "DBGHELP_", "COR_", "CORECLR_", "COMPlus_", "DOTNET_", "PSExecutionPolicyPreference",
    };

    public static void Apply()
    {
        try { UserTempPath = Path.GetTempPath(); } catch { /* keep the default */ }

        string system32 = Environment.SystemDirectory;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string commonFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        string commonFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Set("PSModulePath", string.Join(';',
            Path.Combine(system32, "WindowsPowerShell", "v1.0", "Modules"),
            Path.Combine(programFiles, "WindowsPowerShell", "Modules")));
        Set("SystemRoot", windows);
        Set("windir", windows);
        Set("ComSpec", Path.Combine(system32, "cmd.exe"));
        Set("ProgramFiles", programFiles);
        Set("ProgramW6432", programFiles);
        Set("ProgramFiles(x86)", programFilesX86);
        Set("CommonProgramFiles", commonFiles);
        Set("CommonProgramW6432", commonFiles);
        Set("CommonProgramFiles(x86)", commonFilesX86);
        Set("ProgramData", programData);
        Set("ALLUSERSPROFILE", programData);
        if (ReadMachineVariable("PATHEXT") is { Length: > 0 } pathExt) Set("PATHEXT", pathExt);

        try
        {
            var names = new List<string>();
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string name && StrippedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    names.Add(name);
            }
            foreach (string name in names) Environment.SetEnvironmentVariable(name, null);
        }
        catch
        {
            // best-effort
        }

        if (AdminDataPaths.TryGetArea("Temp", out _) is { } adminTemp)
        {
            Set("TEMP", adminTemp);
            Set("TMP", adminTemp);
        }
    }

    private static void Set(string name, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        try { Environment.SetEnvironmentVariable(name, value); } catch { /* best-effort */ }
    }

    private static string? ReadMachineVariable(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }
}
