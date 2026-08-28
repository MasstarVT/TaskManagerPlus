using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>Enumerates and controls Windows services.</summary>
public sealed class ServiceControlService
{
    /// <summary>#758: services this app declines to offer editable recovery-action configuration
    /// for - core RPC/COM/eventing plumbing so fundamental that a bad recovery-action write (e.g.
    /// "reboot the computer on every failure") could make the machine hard to manage or even hard
    /// to boot to a usable desktop. Deliberately small and conservative - this is a hard block, not
    /// a "quick flag, not a verdict" heuristic. RpcSs/RpcEptMapper/DcomLaunch already can't be
    /// stopped at all (see ServiceRow.CanStop, for the same reason); EventLog is added here too
    /// since losing event logging on a machine already fighting service failures makes diagnosing
    /// the underlying problem itself much harder.</summary>
    public static readonly IReadOnlySet<string> ProtectedCoreServiceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "EventLog",
    };

    public static bool IsProtectedCoreService(string serviceName) => ProtectedCoreServiceNames.Contains(serviceName);

    /// <summary>Builds the current list of services. Safe to call from a background thread.</summary>
    public List<ServiceRow> Sample()
    {
        var pids = ReadServicePids();
        var exitCodes = ReadServiceExitCodes();
        var accounts = ReadServiceAccounts();
        int autoStartDelaySeconds = ReadGlobalAutoStartDelaySeconds();
        var rows = new List<ServiceRow>();

        foreach (var sc in ServiceController.GetServices())
        {
            try
            {
                var row = new ServiceRow
                {
                    ServiceName = sc.ServiceName,
                    DisplayName = sc.DisplayName,
                    Status = sc.Status,
                    Description = ReadDescriptionFromRegistry(sc.ServiceName),
                    IsProtectedCore = IsProtectedCoreService(sc.ServiceName),
                };
                try { row.StartType = sc.StartType; } catch { row.StartType = ServiceStartMode.Manual; }
                if (pids.TryGetValue(sc.ServiceName, out var pid))
                    row.ProcessId = pid;
                if (exitCodes.TryGetValue(sc.ServiceName, out var exitCode))
                    row.ExitCode = exitCode;
                if (accounts.TryGetValue(sc.ServiceName, out var account))
                    row.LogOnAs = account;

                // #755: a single trivial registry read, same cost class as Description above - see
                // ServiceRow.StartTypeDisplay for how this and the machine-wide delay are shown.
                row.IsDelayedAutoStart = ReadDelayedAutostart(sc.ServiceName);
                row.AutoStartDelaySeconds = autoStartDelaySeconds;

                // #37: dependency graph - so a user understands the blast radius before stopping
                // a service. Read fresh every tick, the same "no per-row caching" tradeoff
                // Description above already makes, since dependencies can't change without a
                // reboot/reinstall anyway.
                try { row.DependsOn = sc.ServicesDependedOn.Select(s => s.DisplayName).ToList(); }
                catch { /* leave empty */ }
                try { row.DependentServices = sc.DependentServices.Select(s => s.DisplayName).ToList(); }
                catch { /* leave empty */ }

                rows.Add(row);
            }
            catch
            {
                // Service query failed (permissions, race with uninstall) - skip it.
            }
            finally
            {
                sc.Dispose();
            }
        }

        return rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Round 7 #15: kernel/file-system driver "services" - ServiceController.GetDevices()
    /// is a distinct, already-available .NET API from GetServices() (no WMI needed), covering the
    /// SERVICE_KERNEL_DRIVER/SERVICE_FILE_SYSTEM_DRIVER entries the ordinary Services tab never
    /// shows. Drivers rarely have a meaningful logon account or dependency graph the way a Win32
    /// service does, so those fields are simply left at their defaults here rather than queried.</summary>
    public List<ServiceRow> SampleDrivers()
    {
        var rows = new List<ServiceRow>();
        try
        {
            foreach (var sc in ServiceController.GetDevices())
            {
                try
                {
                    var row = new ServiceRow
                    {
                        ServiceName = sc.ServiceName,
                        DisplayName = sc.DisplayName,
                        Status = sc.Status,
                        Description = ReadDescriptionFromRegistry(sc.ServiceName),
                        IsDriver = true,
                    };
                    try { row.StartType = sc.StartType; } catch { row.StartType = ServiceStartMode.Manual; }
                    rows.Add(row);
                }
                catch
                {
                    // Driver query failed - skip it.
                }
                finally
                {
                    sc.Dispose();
                }
            }
        }
        catch
        {
            // GetDevices() itself unavailable - degrade to an empty driver list.
        }
        return rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Round 7 #17: reverse lookup for a host process pid (almost always svchost.exe, but
    /// also e.g. dllhost.exe/some driver-hosting processes) - which service names currently live
    /// inside it. Reuses the same Win32_Service ProcessId column ReadServicePids already reads,
    /// just grouped the other direction.</summary>
    public static Dictionary<int, List<string>> ReadServicesByPid()
    {
        var result = new Dictionary<int, List<string>>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, ProcessId FROM Win32_Service WHERE ProcessId <> 0");
            foreach (ManagementObject mo in searcher.Get())
            {
                var displayName = mo["DisplayName"] as string ?? mo["Name"] as string;
                if (displayName is null) continue;
                int pid = Convert.ToInt32(mo["ProcessId"]);
                if (!result.TryGetValue(pid, out var list))
                    result[pid] = list = new List<string>();
                list.Add(displayName);
            }
        }
        catch
        {
            // WMI unavailable - callers just see no hosted services for any pid.
        }
        return result;
    }

    /// <summary>Round 7 #16: current StartType + logon account per service, in the shape
    /// SnapshotService needs to extend its baseline capture with service config drift detection.</summary>
    public static List<Models.ServiceConfigSnapshot> ReadServiceConfigs()
    {
        var accounts = ReadServiceAccounts();
        var result = new List<Models.ServiceConfigSnapshot>();
        foreach (var sc in ServiceController.GetServices())
        {
            try
            {
                string startType;
                try { startType = sc.StartType.ToString(); } catch { startType = "Unknown"; }
                accounts.TryGetValue(sc.ServiceName, out var account);
                result.Add(new Models.ServiceConfigSnapshot
                {
                    ServiceName = sc.ServiceName,
                    StartType = startType,
                    LogOnAs = account ?? string.Empty,
                });
            }
            catch { /* skip */ }
            finally { sc.Dispose(); }
        }
        return result;
    }

    /// <summary>Round 7 #14: Win32_Service.StartName - the account a service logs on as. Empty for
    /// most drivers (they have no meaningful logon account); LocalSystem/NT AUTHORITY\...  for
    /// built-ins; a real account name for the minority of services configured to run as something
    /// else, which is exactly the "worth a second look" case ServiceRow.IsNonStandardAccount flags.</summary>
    private static Dictionary<string, string> ReadServiceAccounts()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, StartName FROM Win32_Service");
            foreach (ManagementObject mo in searcher.Get())
            {
                var name = mo["Name"] as string;
                if (name is null) continue;
                result[name] = mo["StartName"] as string ?? string.Empty;
            }
        }
        catch
        {
            // WMI unavailable - LogOnAs stays empty for every row.
        }
        return result;
    }

    private static Dictionary<string, int> ReadServicePids()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, ProcessId FROM Win32_Service WHERE ProcessId <> 0");
            foreach (ManagementObject mo in searcher.Get())
            {
                var name = mo["Name"] as string;
                if (name is null) continue;
                result[name] = Convert.ToInt32(mo["ProcessId"]);
            }
        }
        catch
        {
            // WMI unavailable - PID column will just show 0.
        }
        return result;
    }

    /// <summary>
    /// Win32_Service.ExitCode from each service's last start attempt. Deliberately not filtered
    /// to Automatic-and-not-running services here (that heuristic was tried and is too noisy in
    /// practice - most Windows systems have several Automatic services that are legitimately
    /// stopped most of the time: delayed-auto-start, or "Automatic (Trigger Start)" services that
    /// only run when triggered, e.g. WbioSrvc, MapsBroker. Both report ExitCode 0 when simply not
    /// started yet, same as a real clean stop - so a nonzero ExitCode is the actually-reliable
    /// "this service tried to start and failed" signal, computed per-row in ServiceRow.HasFailedToStart).
    /// </summary>
    private static Dictionary<string, uint> ReadServiceExitCodes()
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, ExitCode FROM Win32_Service");
            foreach (ManagementObject mo in searcher.Get())
            {
                var name = mo["Name"] as string;
                if (name is null) continue;
                result[name] = Convert.ToUInt32(mo["ExitCode"] ?? 0u);
            }
        }
        catch
        {
            // WMI unavailable - every row falls back to ExitCode 0 (never flagged as failed).
        }
        return result;
    }

    private static string ReadDescriptionFromRegistry(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key?.GetValue("Description") as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>#755: DWORD DelayedAutostart under this service's own key - 1 means "Automatic
    /// (Delayed Start)" the way the Services snap-in shows it; absent/0/unreadable all mean an
    /// ordinary Automatic start, never fabricated as delayed.</summary>
    private static bool ReadDelayedAutostart(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key?.GetValue("DelayedAutostart") is int i && i != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>#755: the machine-wide autostart delay - Windows' documented default (120s)
    /// applies whenever the value is absent, not just when it's genuinely unreadable, since an
    /// absent AutoStartDelay is the normal, common case (most machines never set it explicitly).</summary>
    private static int ReadGlobalAutoStartDelaySeconds()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            return key?.GetValue("AutoStartDelay") is int i && i > 0 ? i : 120;
        }
        catch
        {
            return 120;
        }
    }

    public static (bool Success, string? Error) Start(string serviceName)
        => RunControlAction(serviceName, sc => sc.Start(), ServiceControllerStatus.Running);

    public static (bool Success, string? Error) Stop(string serviceName)
        => RunControlAction(serviceName, sc => sc.Stop(), ServiceControllerStatus.Stopped);

    public static (bool Success, string? Error) Restart(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status is not ServiceControllerStatus.Stopped and not ServiceControllerStatus.StopPending)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// #896: OEM cleanup's "Disable" action for a service - there was no existing enable/disable-
    /// by-start-type control anywhere in this app before this chunk (only Start/Stop/Restart
    /// above), so this is a genuinely new small method, added here rather than as a one-off in the
    /// Security tab, so any future caller has one place to toggle a service's start type. Shells
    /// out to `sc.exe config ... start= &lt;type&gt;` - the same "known tool, not raw SCM interop"
    /// tradeoff ReadFailureActionsTextAsync below already takes for qfailure, and NEVER deletes
    /// the service. Returns the PREVIOUS start type (as sc.exe's own start= vocabulary: "auto"/
    /// "demand"/"disabled") so a caller can record enough to Undo by calling this again with that
    /// exact string.
    /// </summary>
    public static (bool Success, string PreviousStartType, string? Error) SetStartupType(string serviceName, string startType)
    {
        string previous = "demand";
        try
        {
            using var sc = new ServiceController(serviceName);
            previous = sc.StartType switch
            {
                ServiceStartMode.Automatic => "auto",
                ServiceStartMode.Disabled => "disabled",
                _ => "demand",
            };
        }
        catch { /* best-effort - Undo may end up guessing "demand" if this couldn't be read */ }

        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"config \"{serviceName}\" start= {startType}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, previous, "couldn't start sc.exe");

            string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(10000))
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return (false, previous, "sc.exe timed out");
            }
            return proc.ExitCode == 0 ? (true, previous, null) : (false, previous, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, previous, ex.Message);
        }
    }

    /// <summary>
    /// Failure/recovery actions (#71) - what Windows does when this service crashes
    /// (auto-restart, run a program, reboot, ...), read via `sc.exe qfailure`. The raw registry
    /// value (SERVICE_FAILURE_ACTIONS, under the service's own key) is an undocumented binary
    /// layout - shelling out to sc.exe, the same tool that already decodes it for `sc qfailure` at
    /// the command line, avoids depending on that layout directly, the same "known Windows tool,
    /// not raw struct interop" tradeoff NetworkDiagnosticsService's `netsh wlan` parsing already
    /// takes. On-demand only (like Processes' module list) - not worth a WMI/registry read on
    /// every 2s tick for every service.
    /// </summary>
    public static async Task<string> ReadFailureActionsTextAsync(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"qfailure \"{serviceName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "(couldn't run sc.exe)";

            // Concurrent async reads + a bounded WaitForExitAsync + Kill()-on-timeout - the same
            // pattern TracerouteService.RunAsync uses, rather than the previous synchronous
            // ReadToEnd() followed by an unchecked WaitForExit(5000), which could deadlock if
            // sc.exe's output filled its pipe buffer before exiting and would otherwise leave the
            // process running past the 5s mark with nothing to kill it.
            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(5000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return "(sc.exe timed out)";
            }

            string output = (await outputTask) + (await errorTask);

            // Strip the "[SC] QueryServiceConfig2 SUCCESS" boilerplate line sc.exe always prints
            // first - everything after it is the actual recovery-action report.
            var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            int start = lines.FindIndex(l => l.Contains("SERVICE_NAME", StringComparison.OrdinalIgnoreCase));
            var body = start >= 0 ? string.Join('\n', lines.Skip(start)) : output;
            return body.Trim().Length == 0 ? "No recovery actions configured." : body.Trim();
        }
        catch (Exception ex)
        {
            return $"(couldn't read recovery actions: {ex.Message})";
        }
    }

    private static (bool Success, string? Error) RunControlAction(
        string serviceName, Action<ServiceController> action, ServiceControllerStatus waitFor)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            action(sc);
            sc.WaitForStatus(waitFor, TimeSpan.FromSeconds(15));
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// #752/#753/#754: one pass over every entry under HKLM\SYSTEM\CurrentControlSet\Services,
    /// computing three related static-registry-read quick flags together rather than three
    /// separate scans (see ServiceInventoryFlags's remarks): a DependOnService value naming a
    /// service that doesn't exist or is Disabled (#752), an ImagePath whose resolved target file no
    /// longer exists (#753 - follows svchost -k/rundll32 through to the hosted
    /// Parameters\ServiceDll), and an unquoted ImagePath containing a space before its .exe
    /// boundary (#754). Walks the registry tree directly rather than ServiceController.GetServices() -
    /// an orphaned/misconfigured entry is exactly the kind least likely to enumerate cleanly through
    /// the Win32 service APIs. On-demand only (ServicesViewModel.RunInventoryAuditCommand) -
    /// hundreds of registry reads plus a File.Exists() per entry is well past CLAUDE.md's "trivial
    /// per-tick read" bar for the polling timer.
    /// </summary>
    public static List<ServiceInventoryFlags> RunInventoryAudit()
    {
        var result = new List<ServiceInventoryFlags>();
        try
        {
            using var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (servicesKey is null) return result;

            var names = servicesKey.GetSubKeyNames();
            var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

            // #752 needs every service's Start value up front, to check a dependency's Start=4
            // (Disabled) without reopening its key a second time per referencing service.
            var startByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                try
                {
                    using var key = servicesKey.OpenSubKey(name);
                    if (key?.GetValue("Start") is int start) startByName[name] = start;
                }
                catch { /* unreadable key - leave its Start unknown, never flagged as a dependency target */ }
            }

            foreach (var name in names)
            {
                try
                {
                    using var key = servicesKey.OpenSubKey(name);
                    if (key is null) continue;

                    var (hasBrokenDep, brokenDepText) = CheckBrokenDependency(key, nameSet, startByName);
                    string? rawImagePath = key.GetValue("ImagePath") as string;
                    var (isOrphaned, orphanedPath) = CheckOrphaned(key, rawImagePath);
                    var (hasUnquoted, original, corrected) = CheckUnquotedPath(rawImagePath);

                    if (!hasBrokenDep && !isOrphaned && !hasUnquoted) continue; // nothing to report for this entry

                    result.Add(new ServiceInventoryFlags
                    {
                        ServiceName = name,
                        HasBrokenDependency = hasBrokenDep,
                        BrokenDependencyText = brokenDepText,
                        IsOrphaned = isOrphaned,
                        OrphanedImagePath = orphanedPath,
                        HasUnquotedPath = hasUnquoted,
                        UnquotedPathOriginal = original,
                        UnquotedPathCorrected = corrected,
                    });
                }
                catch
                {
                    // One unreadable entry, or a race with an uninstall mid-scan, shouldn't drop
                    // the rest of the audit.
                }
            }
        }
        catch
        {
            // Services registry key unavailable (shouldn't happen while elevated) - empty result.
        }
        return result;
    }

    /// <summary>#752: DependOnService names either a real service key name, or (a leading '+' marks
    /// a load-ordering *group* name rather than a specific service, so that entry is skipped -
    /// never misreported as a missing service) one that no longer exists, or one whose Start value
    /// is 4 (Disabled). Either would keep this service from ever starting.</summary>
    private static (bool Flagged, string Text) CheckBrokenDependency(
        RegistryKey key, HashSet<string> allServiceNames, Dictionary<string, int> startByName)
    {
        if (key.GetValue("DependOnService") is not string[] deps || deps.Length == 0)
            return (false, string.Empty);

        var problems = new List<string>();
        foreach (var raw in deps)
        {
            var dep = raw?.Trim() ?? string.Empty;
            if (dep.Length == 0 || dep.StartsWith("+", StringComparison.Ordinal)) continue; // empty REG_MULTI_SZ tail, or a load-ordering group name

            if (!allServiceNames.Contains(dep))
                problems.Add($"{dep} (not installed)");
            else if (startByName.TryGetValue(dep, out var start) && start == 4)
                problems.Add($"{dep} (Disabled)");
        }

        return problems.Count == 0 ? (false, string.Empty) : (true, "Depends on: " + string.Join(", ", problems));
    }

    /// <summary>#753: resolves ImagePath - env vars expanded, \??\ and \SystemRoot\ prefixes
    /// resolved, quotes/args stripped - and for a svchost/rundll32 host, follows through to the
    /// actual hosted binary via Parameters\ServiceDll (or, for rundll32, the inline "dll,entry"
    /// argument) - then reports whether that resolved target file exists. A missing/empty
    /// ImagePath (some driver entries have none at all) is left unflagged, never fabricated as
    /// orphaned.</summary>
    private static (bool Flagged, string MissingPath) CheckOrphaned(RegistryKey key, string? rawImagePath)
    {
        if (string.IsNullOrWhiteSpace(rawImagePath)) return (false, string.Empty);

        string path = Environment.ExpandEnvironmentVariables(rawImagePath.Trim());
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path[@"\SystemRoot\".Length..]);

        string exePath = ExtractExecutablePath(path);
        if (exePath.Length == 0) return (false, string.Empty);

        bool isSvchost = exePath.EndsWith("svchost.exe", StringComparison.OrdinalIgnoreCase);
        bool isRundll32 = exePath.EndsWith("rundll32.exe", StringComparison.OrdinalIgnoreCase);

        if (isSvchost || isRundll32)
        {
            string? hostedDll = null;
            try
            {
                using var paramsKey = key.OpenSubKey("Parameters");
                hostedDll = paramsKey?.GetValue("ServiceDll") as string;
            }
            catch { /* no Parameters subkey - not every svchost/rundll32 entry has one */ }

            if (string.IsNullOrWhiteSpace(hostedDll) && isRundll32)
            {
                // rundll32.exe C:\path\to.dll,EntryPoint - the dll is inline in ImagePath itself.
                int idx = path.IndexOf("rundll32.exe", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string after = path[(idx + "rundll32.exe".Length)..].Trim().TrimStart('"');
                    hostedDll = after.Split(',')[0].Trim().Trim('"');
                }
            }

            if (string.IsNullOrWhiteSpace(hostedDll))
                return (false, string.Empty); // svchost.exe/rundll32.exe itself always exists on a running system - nothing further to check

            string dllPath = Environment.ExpandEnvironmentVariables(hostedDll);
            if (dllPath.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                dllPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), dllPath[@"\SystemRoot\".Length..]);

            return File.Exists(dllPath) ? (false, string.Empty) : (true, dllPath);
        }

        return File.Exists(exePath) ? (false, string.Empty) : (true, exePath);
    }

    /// <summary>Splits a resolved ImagePath command line down to just the executable portion - the
    /// quoted case is unambiguous; the unquoted case tries the whole string first (the common
    /// no-arguments case), then falls back to the first ".exe" boundary.</summary>
    private static string ExtractExecutablePath(string commandLine)
    {
        commandLine = commandLine.Trim();
        if (commandLine.Length == 0) return string.Empty;

        if (commandLine[0] == '"')
        {
            int end = commandLine.IndexOf('"', 1);
            return end > 0 ? commandLine[1..end] : commandLine.Trim('"');
        }

        if (File.Exists(commandLine)) return commandLine;

        int exeIdx = commandLine.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0) return commandLine[..(exeIdx + 4)];

        return commandLine.Split(' ')[0];
    }

    /// <summary>#757: does `exePath` exist as-is, or (for a bare executable name with no directory
    /// component - e.g. a recovery action of "cmd.exe") resolve via the same System32/Windows/PATH
    /// search Windows itself performs when launching a program named without a path? Checking
    /// File.Exists on a bare name alone would false-positive on a perfectly valid recovery action -
    /// the same reasoning ScheduledTaskService.EvaluateTarget/ResolveViaPath already applies to a
    /// Scheduled Task's Task To Run.</summary>
    private static bool ResolvesToAnExistingFile(string exePath)
    {
        if (File.Exists(exePath)) return true;
        if (Path.GetDirectoryName(exePath) is { Length: > 0 }) return false; // has a path - already checked, genuinely missing

        try
        {
            var candidateDirs = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            };
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (pathEnv is not null) candidateDirs.AddRange(pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries));

            return candidateDirs.Any(dir => File.Exists(Path.Combine(dir, exePath)));
        }
        catch
        {
            return false; // malformed PATH entry or similar - couldn't confirm, falls back to "missing"
        }
    }

    /// <summary>#754: the classic unquoted-service-path pattern - an unquoted ImagePath whose
    /// file-path portion (before its .exe extension) contains a space lets Windows try each
    /// space-delimited prefix as a candidate executable in turn, so a file planted at one of those
    /// shorter paths can hijack the launch. The check runs against the *expanded* path (services.exe
    /// itself expands %VARS% before calling CreateProcess, so a %ProgramFiles%\... reference is
    /// exactly as exploitable as a literal space), while UnquotedPathCorrected quotes the same
    /// boundary in the original, unexpanded registry value so a %VAR%-based path stays portable
    /// when pasted back into ImagePath. "Quick flag, not a verdict" - the pattern existing doesn't
    /// mean anything is actually planted there.</summary>
    private static (bool Flagged, string Original, string Corrected) CheckUnquotedPath(string? rawImagePath)
    {
        if (string.IsNullOrWhiteSpace(rawImagePath)) return (false, string.Empty, string.Empty);

        string trimmed = rawImagePath.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '"') return (false, string.Empty, string.Empty);

        string expanded = Environment.ExpandEnvironmentVariables(trimmed);
        int expandedExeIdx = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (expandedExeIdx < 0) return (false, string.Empty, string.Empty);
        if (!expanded[..(expandedExeIdx + 4)].Contains(' ')) return (false, string.Empty, string.Empty);

        int rawExeIdx = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        string corrected = rawExeIdx >= 0
            ? $"\"{trimmed[..(rawExeIdx + 4)]}\"" + trimmed[(rawExeIdx + 4)..]
            : $"\"{trimmed}\"";

        return (true, trimmed, corrected);
    }

    /// <summary>
    /// #756: trigger-start conditions (device arrival, IP address availability, domain join, group
    /// policy, firewall port event, custom ETW, ...) via `sc.exe qtriggerinfo` - the documented way
    /// to read SERVICE_TRIGGER_INFO, since the raw registry encoding (the service's own TriggerInfo
    /// subkey) is an undocumented binary layout, the same "known Windows tool, not raw struct
    /// interop" tradeoff ReadFailureActionsTextAsync already takes for `sc qfailure`. On-demand
    /// only, same cadence as recovery actions - not worth a shell-out on every tick for every
    /// service.
    /// </summary>
    public static async Task<string> ReadTriggerInfoTextAsync(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"qtriggerinfo \"{serviceName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return "(couldn't run sc.exe)";

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(5000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return "(sc.exe timed out)";
            }

            string output = (await outputTask) + (await errorTask);
            return ParseTriggerInfo(output);
        }
        catch (Exception ex)
        {
            return $"(couldn't read trigger info: {ex.Message})";
        }
    }

    /// <summary>Documented Microsoft device-interface-class GUIDs worth naming in plain English -
    /// deliberately a short, high-confidence list (GUID_BTHPORT_DEVICE_INTERFACE confirmed live
    /// against BthServ's own `sc qtriggerinfo` output; GUID_DEVINTERFACE_USB_DEVICE and
    /// GUID_DEVINTERFACE_DISK are long-documented WDK constants). Any GUID not in here is shown
    /// as-is rather than guessed at, per CLAUDE.md's "degrade, never fabricate".</summary>
    private static readonly Dictionary<string, string> KnownDeviceInterfaceGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0850302a-b344-4fda-9be9-90576b8d46f0"] = "a Bluetooth radio arrives",
        ["a5dcbf10-6530-11d2-901f-00c04fb951ed"] = "a USB device arrives",
        ["53f56307-b6bf-11d0-94f2-00a0c91efb8b"] = "a disk device arrives",
    };

    private static readonly Regex TriggerActionRegex = new(@"^(START|STOP) SERVICE\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex TriggerLineRegex = new(@"^([A-Z][A-Z \-]*?)\s*:\s*(.+)$");
    private static readonly Regex TriggerSubtypeRegex = new(@"\[([^\]]+)\]\s*$");

    /// <summary>Turns `sc qtriggerinfo`'s plain-text report into one "Automatic (Trigger Start): ..."
    /// line per trigger - see KnownDeviceInterfaceGuids's remarks for why device-arrival triggers
    /// only get a friendly name for a small, confirmed set of GUIDs.</summary>
    private static string ParseTriggerInfo(string output)
    {
        if (output.Contains("has not registered for any start or stop triggers", StringComparison.OrdinalIgnoreCase))
            return "No trigger-start configuration - this service only starts via its Start type, a dependency, or manually.";
        if (output.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("couldn't run", StringComparison.OrdinalIgnoreCase))
            return output;

        var lines = output.Replace("\r\n", "\n").Split('\n');
        var descriptions = new List<string>();
        string? action = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("[SC]", StringComparison.Ordinal) || line.StartsWith("SERVICE_NAME", StringComparison.OrdinalIgnoreCase))
                continue;

            var actionMatch = TriggerActionRegex.Match(line);
            if (actionMatch.Success)
            {
                action = actionMatch.Groups[1].Value.Equals("START", StringComparison.OrdinalIgnoreCase) ? "starts" : "stops";
                continue;
            }

            if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase)) continue; // trigger-specific payload, not needed for the plain-English summary
            if (action is null) continue;

            var lineMatch = TriggerLineRegex.Match(line);
            if (!lineMatch.Success) continue;

            string label = lineMatch.Groups[1].Value.Trim().ToUpperInvariant();
            string rest = lineMatch.Groups[2].Value.Trim();
            string guid = TriggerSubtypeRegex.Replace(rest, string.Empty).Trim();
            var subtypeMatch = TriggerSubtypeRegex.Match(rest);
            string subtype = subtypeMatch.Success ? subtypeMatch.Groups[1].Value.Trim() : string.Empty;

            string what = label switch
            {
                "DEVICE INTERFACE ARRIVAL" => KnownDeviceInterfaceGuids.TryGetValue(guid, out var friendly)
                    ? friendly
                    : $"a device of interface class {{{guid}}} arrives",
                "IP ADDRESS AVAILABILITY" => "the machine acquires a network IP address",
                "DOMAIN JOIN" or "DOMAIN JOINED STATUS" => "the machine's domain-join status changes",
                "FIREWALL PORT EVENT" => subtype.Length > 0
                    ? $"a firewall {subtype.ToLowerInvariant()} event occurs"
                    : "a firewall port event occurs",
                "GROUP POLICY" => "a Group Policy update is applied",
                "NETWORK EVENT" => subtype.Length > 0
                    ? $"a network event ({subtype.ToLowerInvariant()}) occurs"
                    : "a network event occurs",
                "CUSTOM" or "CUSTOM SYSTEM STATE CHANGE" => "a custom ETW trigger event occurs",
                "AGGREGATE SERVICE TRIGGER" => "another service in its trigger group starts",
                _ => $"{label.ToLowerInvariant()} ({guid})",
            };

            descriptions.Add($"Automatic (Trigger Start): {action} when {what}.");
        }

        if (descriptions.Count == 0)
            return "Trigger-start is configured, but its details couldn't be parsed from sc.exe's output.";

        return string.Join("\n", descriptions.Distinct());
    }

    /// <summary>#753: `sc delete` for an orphaned service row, offered only behind a confirmation
    /// dialog in ServicesViewModel.DeleteOrphanedServiceCommand - the same concurrent-read/bounded-
    /// wait/kill-on-timeout shell-out pattern as every other process launched in this file.</summary>
    public static async Task<(bool Success, string? Error)> DeleteAsync(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"delete \"{serviceName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "couldn't run sc.exe");

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(10000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return (false, "sc.exe timed out");
            }

            string output = (await outputTask) + (await errorTask);
            return proc.ExitCode == 0 ? (true, null) : (false, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Shells to sc.exe and captures combined stdout+stderr, bounded by a real timeout -
    /// the same concurrent-read/bounded-wait/kill-on-timeout pattern every other sc.exe call in this
    /// file already uses (see ReadFailureActionsTextAsync's remarks), factored out once a second and
    /// third caller (SetFailureActionsAsync, QueryExAsync) needed the identical shape.</summary>
    private static async Task<(bool Success, string Output, int ExitCode)> RunScAsync(string args, int timeoutMs = 10000)
    {
        var psi = new ProcessStartInfo("sc.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return (false, "(couldn't run sc.exe)", -1);

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { /* best-effort */ }
            return (false, "(sc.exe timed out)", -1);
        }

        string output = (await outputTask) + (await errorTask);
        return (proc.ExitCode == 0, output, proc.ExitCode);
    }

    #region #757 - Bulk recovery-action audit

    /// <summary>
    /// #757: runs `sc qfailure` across every service, batched off the UI thread with bounded
    /// concurrency (a few hundred sc.exe process spawns run serially would take a while; unbounded
    /// concurrency would just as easily flood the process table), reporting three outliers: no
    /// recovery action configured (Automatic-start services only - see EvaluateRecoveryOutliers),
    /// configured to reboot the computer on failure, and a "run a program" action pointing at a
    /// missing binary. Reuses ReadFailureActionsTextAsync's own text output rather than a second,
    /// differently-shaped sc.exe call - the per-row "Recovery actions" button and this bulk audit
    /// read exactly the same data.
    /// </summary>
    public static async Task<List<ServiceRecoveryAuditEntry>> RunRecoveryActionAuditAsync(
        IReadOnlyList<(string ServiceName, bool IsAutomaticStart)> services,
        IProgress<(int Done, int Total)>? progress = null)
    {
        var result = new ConcurrentBag<ServiceRecoveryAuditEntry>();
        int total = services.Count;
        int done = 0;

        using var gate = new SemaphoreSlim(8);
        var tasks = services.Select(async svc =>
        {
            await gate.WaitAsync();
            try
            {
                string text = await ReadFailureActionsTextAsync(svc.ServiceName);
                var entry = EvaluateRecoveryOutliers(svc.ServiceName, svc.IsAutomaticStart, text);
                if (entry is not null) result.Add(entry);
            }
            finally
            {
                int d = Interlocked.Increment(ref done);
                progress?.Report((d, total));
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return result.OrderBy(e => e.ServiceName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly Regex RunProcessCommandLineRegex = new(@"COMMAND_LINE\s*:\s*(.*)", RegexOptions.IgnoreCase);

    /// <summary>#757: parses one service's `sc qfailure` text (see ReadFailureActionsTextAsync) into
    /// outlier flags. No "FAILURE_ACTIONS" section at all means no recovery action is configured
    /// (sc.exe simply omits that line rather than printing an empty one - verified against a live
    /// machine's own trigger-start/manual services). A "RUN PROCESS" action is checked against
    /// COMMAND_LINE resolved the same way RunInventoryAudit resolves an ImagePath.</summary>
    private static ServiceRecoveryAuditEntry? EvaluateRecoveryOutliers(string serviceName, bool isAutomaticStart, string qfailureText)
    {
        // ReadFailureActionsTextAsync's own error/timeout fallbacks are always wrapped in
        // parentheses (e.g. "(sc.exe timed out)") - never mistake "couldn't read this" for "no
        // recovery action configured" and report a false outlier.
        if (qfailureText.Length == 0 || qfailureText.StartsWith('(')) return null;

        bool hasFailureActionsSection = qfailureText.Contains("FAILURE_ACTIONS", StringComparison.OrdinalIgnoreCase);
        bool noneConfigured = isAutomaticStart && !hasFailureActionsSection;
        bool rebootsOnFailure = false;
        bool runsMissingProgram = false;
        string missingProgramPath = string.Empty;

        if (hasFailureActionsSection)
        {
            var lines = qfailureText.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim());
            foreach (var line in lines)
            {
                if (line.StartsWith("REBOOT", StringComparison.OrdinalIgnoreCase))
                {
                    rebootsOnFailure = true;
                }
                else if (line.StartsWith("RUN PROCESS", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("RUN COMMAND", StringComparison.OrdinalIgnoreCase))
                {
                    var cmdMatch = RunProcessCommandLineRegex.Match(qfailureText);
                    string cmdLine = cmdMatch.Success ? cmdMatch.Groups[1].Value.Trim() : string.Empty;
                    if (cmdLine.Length == 0) continue;

                    string exePath = ExtractExecutablePath(Environment.ExpandEnvironmentVariables(cmdLine));
                    if (exePath.Length > 0 && !ResolvesToAnExistingFile(exePath))
                    {
                        runsMissingProgram = true;
                        missingProgramPath = exePath;
                    }
                }
            }
        }

        if (!noneConfigured && !rebootsOnFailure && !runsMissingProgram) return null;

        return new ServiceRecoveryAuditEntry
        {
            ServiceName = serviceName,
            NoRecoveryConfigured = noneConfigured,
            RebootsOnFailure = rebootsOnFailure,
            RunsMissingProgram = runsMissingProgram,
            MissingProgramPath = missingProgramPath,
        };
    }

    #endregion

    #region #758 - Editable recovery actions

    /// <summary>
    /// #758: writes recovery-action config via `sc failure "&lt;name&gt;" reset= &lt;seconds&gt;
    /// actions= &lt;action1&gt;/&lt;delay1&gt;/&lt;action2&gt;/&lt;delay2&gt;/&lt;action3&gt;/&lt;delay3&gt;`
    /// - the exact command is built once by the caller (ServicesViewModel) and shown verbatim in the
    /// confirmation dialog before this runs it, matching CLAUDE.md's "mutating actions require
    /// confirmation with the exact command shown" rule. `sc failure /?` documents only run/restart/
    /// reboot as valid action keywords (verified live) - there is no "none" keyword, so a "None"
    /// selection is represented by an empty action slot between slashes (e.g.
    /// "restart/60000/restart/60000//0"), the documented way to write SC_ACTION_NONE for that step.
    /// Declines to run at all for a ServiceControlService.IsProtectedCoreService name - defense in
    /// depth alongside ServicesViewModel already gating the command's CanExecute on the same check.
    /// </summary>
    public static async Task<(bool Success, string? Error)> SetFailureActionsAsync(string serviceName, string args)
    {
        if (IsProtectedCoreService(serviceName))
            return (false, $"{serviceName} is a protected core service - this app declines to change its recovery actions.");

        var (success, output, _) = await RunScAsync(args, timeoutMs: 10000);
        return success ? (true, null) : (false, output.Trim().Length > 0 ? output.Trim() : "sc.exe reported an error");
    }

    #endregion

    #region #761/#762 - svchost group breakdown and split threshold

    private static readonly Regex ImagePathGroupRegex = new(@"-k\s+(\S+)", RegexOptions.IgnoreCase);

    /// <summary>
    /// #761: maps every svchost.exe host group under
    /// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Svchost to the currently-running services
    /// actually hosted under it (the Svchost key's REG_MULTI_SZ values list group *membership* -
    /// which services are eligible for a group - not a live mapping, so each candidate service's own
    /// ImagePath is checked for a matching `-k &lt;group&gt;` argument before counting it as
    /// currently hosted there), then rolls up CPU time/working set/handle count/thread count per
    /// *process* (a group can be split across more than one svchost.exe instance - see
    /// SvcHostSplitInfo) using the PIDs ReadServicePids already collects. On-demand only
    /// (ServicesViewModel.ShowSvcHostGroups) - a registry read plus a Process snapshot per running
    /// host is more work than the polling timer's per-tick budget.
    /// </summary>
    public static List<SvcHostGroupInfo> ReadSvchostGroups()
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Svchost");
            if (key is not null)
            {
                foreach (var valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is string[] members)
                        groups[valueName] = members.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
                }
            }
        }
        catch
        {
            // Key unavailable - degrade to an empty group list, same as every other registry read here.
        }

        var pidsByService = ReadServicePids();
        var result = new List<SvcHostGroupInfo>();

        foreach (var (groupName, members) in groups)
        {
            var runningPids = new HashSet<int>();
            var runningServices = new List<string>();
            foreach (var svc in members)
            {
                if (!pidsByService.TryGetValue(svc, out var pid) || pid == 0) continue;
                if (!ImagePathClaimsGroup(svc, groupName)) continue;
                runningServices.Add(svc);
                runningPids.Add(pid);
            }

            if (members.Count == 0 && runningPids.Count == 0) continue;

            long ws = 0;
            int handles = 0, threads = 0;
            TimeSpan cpu = TimeSpan.Zero;
            foreach (var pid in runningPids)
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    ws += proc.WorkingSet64;
                    handles += proc.HandleCount;
                    threads += proc.Threads.Count;
                    cpu += proc.TotalProcessorTime;
                }
                catch
                {
                    // Process exited mid-scan, or access denied to a protected host - skip it,
                    // same "one entry's failure doesn't drop the rest" pattern as RunInventoryAudit.
                }
            }

            result.Add(new SvcHostGroupInfo
            {
                GroupName = groupName,
                ServiceNames = members,
                RunningServiceNames = runningServices,
                ProcessIds = runningPids.ToList(),
                TotalWorkingSetBytes = ws,
                TotalHandleCount = handles,
                TotalThreadCount = threads,
                TotalCpuTime = cpu,
            });
        }

        return result.OrderByDescending(g => g.TotalWorkingSetBytes).ToList();
    }

    /// <summary>#761: does this service's own ImagePath actually carry `-k &lt;groupName&gt;`? A
    /// service can be listed as a group member in the Svchost key without currently running under
    /// that flag (or at all), so group membership alone isn't enough to attribute a running PID.</summary>
    private static bool ImagePathClaimsGroup(string serviceName, string groupName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            string? imagePath = key?.GetValue("ImagePath") as string;
            if (imagePath is null) return false;

            var match = ImagePathGroupRegex.Match(imagePath);
            return match.Success && match.Groups[1].Value.Equals(groupName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>#762: reads SvcHostSplitThresholdInKB and this machine's total RAM (reusing
    /// BcdInspectorService.ReadSystemTotals's existing GlobalMemoryStatusEx call rather than a
    /// second P/Invoke) to report whether per-service svchost splitting is active.</summary>
    public static SvcHostSplitInfo ReadSvcHostSplitInfo()
    {
        long? configured = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            if (key?.GetValue("SvcHostSplitThresholdInKB") is int i && i > 0) configured = i;
        }
        catch
        {
            // Key unavailable - degrade to "not configured", same as every other registry read here.
        }

        var (_, totalRamBytes) = BcdInspectorService.ReadSystemTotals();
        return new SvcHostSplitInfo { ConfiguredThresholdKb = configured, TotalRamKb = totalRamBytes / 1024 };
    }

    #endregion

    #region #763 - Hung-service diagnosis

    private static readonly Regex QueryExStateRegex = new(@"STATE\s*:\s*(\d+)\s+(\S+)", RegexOptions.IgnoreCase);
    private static readonly Regex QueryExCheckpointRegex = new(@"CHECKPOINT\s*:\s*0x([0-9A-Fa-f]+)", RegexOptions.IgnoreCase);
    private static readonly Regex QueryExWaitHintRegex = new(@"WAIT_HINT\s*:\s*0x([0-9A-Fa-f]+)", RegexOptions.IgnoreCase);
    private static readonly Regex QueryExPidRegex = new(@"PID\s*:\s*(\d+)", RegexOptions.IgnoreCase);

    private readonly record struct QueryExSnapshot(string StateText, uint Checkpoint, uint WaitHintMs, int Pid);

    /// <summary>#763: one `sc queryex &lt;name&gt;` snapshot - CHECKPOINT/WAIT_HINT are only
    /// meaningful while STATE is START_PENDING/STOP_PENDING (Windows reports 0x0 for both once a
    /// service settles into RUNNING/STOPPED), which is exactly the case DiagnoseHangAsync cares
    /// about.</summary>
    private static async Task<QueryExSnapshot?> QueryExAsync(string serviceName)
    {
        var (success, output, _) = await RunScAsync($"queryex \"{serviceName}\"", timeoutMs: 5000);
        if (!success) return null;

        var stateMatch = QueryExStateRegex.Match(output);
        if (!stateMatch.Success) return null;

        uint checkpoint = QueryExCheckpointRegex.Match(output) is { Success: true } cp ? Convert.ToUInt32(cp.Groups[1].Value, 16) : 0;
        uint waitHint = QueryExWaitHintRegex.Match(output) is { Success: true } wh ? Convert.ToUInt32(wh.Groups[1].Value, 16) : 0;
        int pid = QueryExPidRegex.Match(output) is { Success: true } p ? int.Parse(p.Groups[1].Value) : 0;

        return new QueryExSnapshot(stateMatch.Groups[2].Value.ToUpperInvariant(), checkpoint, waitHint, pid);
    }

    /// <summary>#763: `HKLM\SYSTEM\CurrentControlSet\Control\ServicesPipeTimeout` - absent means
    /// Windows' documented 30-second default applies, the same "absent is the common case, not an
    /// error" tradeoff ReadGlobalAutoStartDelaySeconds already takes for AutoStartDelay.</summary>
    private static int ReadServicesPipeTimeoutMs()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
            return key?.GetValue("ServicesPipeTimeout") is int i && i > 0 ? i : 30000;
        }
        catch
        {
            return 30000;
        }
    }

    /// <summary>
    /// #763: takes two `sc queryex` CHECKPOINT samples ~3 seconds apart to tell "still making
    /// progress" from "genuinely stuck" for a service in START_PENDING/STOP_PENDING, rather than
    /// guessing off one snapshot - pairs the result with ServicesPipeTimeout (context for how long
    /// Windows itself will wait before giving up) and the caller-supplied PendingDuration (from
    /// ServicesViewModel's own live-observed "since when has this row been pending" tracking - never
    /// fabricated here).
    /// </summary>
    public static async Task<HungServiceDiagnosis> DiagnoseHangAsync(string serviceName, TimeSpan? pendingDuration)
    {
        var first = await QueryExAsync(serviceName);
        if (first is null)
            return new HungServiceDiagnosis { ServiceName = serviceName, Error = "couldn't query service state (sc queryex failed or timed out)" };

        bool isPending = first.Value.StateText is "START_PENDING" or "STOP_PENDING";
        if (!isPending)
        {
            return new HungServiceDiagnosis
            {
                ServiceName = serviceName,
                IsPending = false,
                StateText = first.Value.StateText,
                HostProcessId = first.Value.Pid,
            };
        }

        await Task.Delay(3000);
        var second = await QueryExAsync(serviceName);
        bool advancing = second is not null && second.Value.Checkpoint > first.Value.Checkpoint;

        return new HungServiceDiagnosis
        {
            ServiceName = serviceName,
            IsPending = true,
            StateText = second?.StateText ?? first.Value.StateText,
            CheckpointAdvancing = advancing,
            Checkpoint = second?.Checkpoint ?? first.Value.Checkpoint,
            WaitHintMs = first.Value.WaitHintMs,
            ServicesPipeTimeoutMs = ReadServicesPipeTimeoutMs(),
            HostProcessId = second?.Pid ?? first.Value.Pid,
            PendingDuration = pendingDuration,
        };
    }

    #endregion

    #region #777 - Update service-stack health check

    /// <summary>#777: the services Windows Update itself depends on to function at all - a Disabled
    /// start type on any one of these silently breaks updates the same way a dead WSUS pointer
    /// does (#776), and is the kind of thing a "block Windows Update" script or an over-eager
    /// service-trimming guide leaves behind.</summary>
    private static readonly string[] UpdateStackServiceNames =
    {
        "wuauserv", "BITS", "CryptSvc", "msiserver", "TrustedInstaller", "UsoSvc", "WaaSMedicSvc", "DoSvc",
    };

    /// <summary>#777: targeted per-name ServiceController reads (not a full Sample() enumeration -
    /// eight named lookups are far cheaper than building every service's full ServiceRow) for start
    /// type and current state. A name that doesn't resolve at all (uninstalled/renamed component) is
    /// reported as IsMissing rather than silently dropped, since a missing update-stack service is
    /// itself worth flagging.</summary>
    public static List<UpdateServiceHealthEntry> ReadUpdateServiceStackHealth()
    {
        var result = new List<UpdateServiceHealthEntry>();
        foreach (var name in UpdateStackServiceNames)
        {
            try
            {
                using var sc = new ServiceController(name);
                string startTypeText;
                bool isDisabled;
                try
                {
                    var startType = sc.StartType;
                    startTypeText = startType.ToString();
                    isDisabled = startType == ServiceStartMode.Disabled;
                }
                catch
                {
                    startTypeText = "Unknown";
                    isDisabled = false;
                }

                string statusText;
                try { statusText = sc.Status.ToString(); }
                catch { statusText = "Unknown"; }

                result.Add(new UpdateServiceHealthEntry
                {
                    ServiceName = name,
                    DisplayName = TryGetDisplayName(sc, name),
                    StartTypeText = startTypeText,
                    StatusText = statusText,
                    IsDisabled = isDisabled,
                });
            }
            catch
            {
                // ServiceController construction itself throws when the service name doesn't
                // resolve at all (not merely inaccessible) - report it as missing rather than
                // silently dropping it, since that's itself a worthwhile flag for this card.
                result.Add(new UpdateServiceHealthEntry { ServiceName = name, DisplayName = name, IsMissing = true });
            }
        }
        return result;
    }

    private static string TryGetDisplayName(ServiceController sc, string fallback)
    {
        try { return sc.DisplayName; }
        catch { return fallback; }
    }

    /// <summary>#777: Windows' own documented default start type for each update-stack service -
    /// restores every one of them in one confirmed action rather than making the user hunt down and
    /// fix each Disabled entry individually. `sc config &lt;name&gt; start= &lt;type&gt;` is the same
    /// documented way SetFailureActionsAsync (#758) already writes service config, since
    /// ServiceController itself exposes no start-type setter.</summary>
    private static readonly (string ServiceName, string ScStartArg)[] UpdateStackDefaultStartTypes =
    {
        ("wuauserv", "demand"), ("BITS", "demand"), ("CryptSvc", "auto"), ("msiserver", "demand"),
        ("TrustedInstaller", "demand"), ("UsoSvc", "auto"), ("WaaSMedicSvc", "demand"), ("DoSvc", "auto"),
    };

    /// <summary>#777: runs `sc config` for every update-stack service in turn, continuing past an
    /// individual failure (one missing/protected service shouldn't block restoring the rest) and
    /// reporting every failure's own message. Confirmed by the caller (WindowsHealthViewModel)
    /// before this runs, matching CLAUDE.md's "mutating actions require confirmation with the exact
    /// effect shown" rule.</summary>
    public static async Task<(bool Success, string? Error)> RestoreUpdateServiceStackDefaultsAsync()
    {
        var errors = new List<string>();
        foreach (var (name, startArg) in UpdateStackDefaultStartTypes)
        {
            var (success, output, _) = await RunScAsync($"config \"{name}\" start= {startArg}");
            if (!success) errors.Add($"{name}: {(output.Trim().Length > 0 ? output.Trim() : "sc.exe reported an error")}");
        }
        return errors.Count == 0 ? (true, null) : (false, string.Join("; ", errors));
    }

    #endregion
}
