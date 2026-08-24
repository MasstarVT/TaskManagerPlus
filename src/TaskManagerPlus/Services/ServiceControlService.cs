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
