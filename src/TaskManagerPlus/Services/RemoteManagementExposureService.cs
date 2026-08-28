using System.ServiceProcess;
using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 19, #886: remote management exposure - WinRM (also the ride-along proxy for PowerShell
/// Remoting, which is built on WinRM), Remote Registry, RPC Locator, Remote Assistance, and an
/// OpenSSH Server presence/state check. Reports service state only (reusing
/// ServiceController the same way DefenderService/ServiceControlService already do elsewhere in
/// this app) - no toggle here, per #886's own text: "so the user can act via the EXISTING Services
/// tab."
/// </summary>
public static class RemoteManagementExposureService
{
    public sealed record RemoteManagementItem(string Name, string? ServiceName, string StatusText, string Note);

    public static List<RemoteManagementItem> Scan()
    {
        var items = new List<RemoteManagementItem>
        {
            BuildServiceItem("WinRM (Windows Remote Management)", "WinRM",
                "Also the transport PowerShell Remoting (Enter-PSSession/Invoke-Command) rides on - if WinRM is running, remote PowerShell access is generally possible too, subject to its own authentication/firewall rules."),
            BuildServiceItem("Remote Registry", "RemoteRegistry",
                "Lets a remote, authenticated user read/edit this machine's registry over the network - rarely needed outside a managed domain environment."),
            BuildServiceItem("RPC Locator", "RpcLocator",
                "A legacy RPC name-service helper, often not installed at all on modern Windows - that's expected, not a problem."),
            BuildOpenSshItem(),
        };

        int? remoteAssistance = ReadDword(@"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp");
        items.Add(new RemoteManagementItem(
            "Remote Assistance",
            null,
            remoteAssistance switch { null => "Not set (Windows default: enabled)", 0 => "Disabled", _ => "Enabled" },
            "Lets someone you've invited (or, if misconfigured, an attacker with local access) remotely view/control this desktop - change via System Properties > Remote, or the fAllowToGetHelp registry value directly."));

        return items;
    }

    private static RemoteManagementItem BuildServiceItem(string displayName, string serviceName, string note)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            // Touching .Status forces the lazy service-existence check - throws InvalidOperationException
            // if the service isn't installed at all, the same "not installed is a normal, expected
            // outcome" case #886's own text calls out for RPC Locator specifically.
            var status = sc.Status;
            return new RemoteManagementItem(displayName, serviceName, $"{status} (start type: {sc.StartType})", note);
        }
        catch (InvalidOperationException)
        {
            return new RemoteManagementItem(displayName, serviceName, "Not installed", note);
        }
        catch (Exception ex)
        {
            return new RemoteManagementItem(displayName, serviceName, $"Unknown ({ex.Message})", note);
        }
    }

    /// <summary>OpenSSH Server ships as an optional Windows feature whose service is literally
    /// named "sshd" once installed - same existence-check pattern as the other services here.</summary>
    private static RemoteManagementItem BuildOpenSshItem() =>
        BuildServiceItem("OpenSSH Server (sshd)", "sshd",
            "A full remote shell, if installed and running - make sure key-based auth (not password auth) is configured if this is intentionally on.");

    private static int? ReadDword(string subKey, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKey);
            return key?.GetValue(valueName) as int?;
        }
        catch
        {
            return null;
        }
    }
}
