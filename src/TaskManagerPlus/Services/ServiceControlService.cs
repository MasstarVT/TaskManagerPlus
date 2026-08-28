using System.Diagnostics;
using System.Management;
using System.ServiceProcess;
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
}
