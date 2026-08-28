using System.Diagnostics;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #799: diffs a process's own PATH/TEMP (read via ProcessEnvironmentService's PEB walk - the
/// only way to read an arbitrary already-running process's environment, see that service's own
/// remarks) against the machine+user environment as it stands right now
/// (EnvironmentHealthService.ReadEffectivePathAndTemp). A process only ever inherits its
/// environment once, at CreateProcess time - it never picks up a later PATH/TEMP edit on its own,
/// which is exactly the gap this flags: "this process inherited an environment from before your
/// last change - restart it to pick up the new value."
/// </summary>
public static class ProcessEnvironmentDriftService
{
    /// <summary>Checks one already-selected process (the Processes tab's own "View environment"
    /// flow, #799's per-row use) - cheap, a single PEB walk.</summary>
    public static ProcessEnvironmentDrift CheckSingle(int pid, string processName, List<string> processEnvironment)
    {
        if (processEnvironment.Count == 0 || !processEnvironment.Any(e => e.Contains('=')))
        {
            return new ProcessEnvironmentDrift { Pid = pid, ProcessName = processName, HasDrift = false, Detail = "Environment couldn't be read for this process (see above) - drift can't be determined." };
        }

        var (effectivePath, effectiveTemp) = EnvironmentHealthService.ReadEffectivePathAndTemp();
        return BuildDrift(pid, processName, processEnvironment, effectivePath, effectiveTemp);
    }

    /// <summary>#799's Windows Health tab summary: an explicit, on-demand sweep of every currently
    /// running process (a PEB walk per process, so - like this app's other heavier on-demand scans,
    /// e.g. DISM's component-store analysis - this is gated behind its own button, never run on a
    /// tick). Best-effort: a process this app can't open (protected/elevated beyond this app's own
    /// elevation, or one that exits mid-scan) is silently skipped rather than counted as drifted.</summary>
    public static async Task<(int Checked, List<ProcessEnvironmentDrift> Drifted)> ScanAllAsync()
    {
        return await Task.Run(() =>
        {
            var (effectivePath, effectiveTemp) = EnvironmentHealthService.ReadEffectivePathAndTemp();
            var drifted = new List<ProcessEnvironmentDrift>();
            int checkedCount = 0;

            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return (0, drifted); }

            foreach (var proc in processes)
            {
                try
                {
                    int pid = proc.Id;
                    string name = proc.ProcessName;
                    var env = ProcessEnvironmentService.Read(pid);
                    // ProcessEnvironmentService degrades to a single explanatory placeholder line
                    // (no "=" in it) for anything it couldn't read - those aren't real environment
                    // data, so they're not counted as "checked" at all.
                    if (env.Count == 0 || !env.Any(e => e.Contains('='))) continue;

                    checkedCount++;
                    var drift = BuildDrift(pid, name, env, effectivePath, effectiveTemp);
                    if (drift.HasDrift) drifted.Add(drift);
                }
                catch { /* one process shouldn't abort the whole sweep */ }
                finally { proc.Dispose(); }
            }

            return (checkedCount, drifted);
        }).ConfigureAwait(false);
    }

    private static ProcessEnvironmentDrift BuildDrift(int pid, string processName, List<string> processEnvironment, string effectivePath, string effectiveTemp)
    {
        string? procPath = FindValue(processEnvironment, "PATH");
        string? procTemp = FindValue(processEnvironment, "TEMP") ?? FindValue(processEnvironment, "TMP");

        var mismatches = new List<string>();
        if (procPath is not null && !PathsEquivalent(procPath, effectivePath))
            mismatches.Add("PATH");
        if (procTemp is not null && !string.Equals(procTemp.TrimEnd('\\'), effectiveTemp.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            mismatches.Add("TEMP");

        bool hasDrift = mismatches.Count > 0;
        string detail = hasDrift
            ? $"This process's {string.Join(" and ", mismatches)} no longer match{(mismatches.Count == 1 ? "es" : "")} the current machine+user value - it inherited its environment before the last change. Restart it to pick up the new value."
            : "Matches the current machine+user environment.";

        return new ProcessEnvironmentDrift { Pid = pid, ProcessName = processName, HasDrift = hasDrift, Detail = detail };
    }

    private static string? FindValue(List<string> env, string name)
    {
        string prefix = name + "=";
        var line = env.FirstOrDefault(e => e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return line?[prefix.Length..];
    }

    /// <summary>Order-insensitive comparison, since a process's inherited PATH segment order can
    /// legitimately differ slightly from a freshly re-composed one without anything actually being
    /// "wrong" - what matters for #799's purpose is whether the *set* of directories changed.</summary>
    private static bool PathsEquivalent(string a, string b)
    {
        var setA = a.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\\').ToLowerInvariant()).ToHashSet();
        var setB = b.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\\').ToLowerInvariant()).ToHashSet();
        return setA.SetEquals(setB);
    }
}
