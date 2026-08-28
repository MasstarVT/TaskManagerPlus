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
}
