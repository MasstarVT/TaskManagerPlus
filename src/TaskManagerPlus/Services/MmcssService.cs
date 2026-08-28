using System.ServiceProcess;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #269: MMCSS (Multimedia Class Scheduler Service) and multimedia-scheduling audit -
/// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile's
/// SystemResponsiveness/NetworkThrottlingIndex values, the per-task scheduling profile under
/// \Tasks\{Audio,Games,Pro Audio}, and whether the MMCSS service itself is running - reusing
/// System.ServiceProcess.ServiceController, the same documented API ServiceControlService already
/// queries every other service's status with (Round-trip through that class isn't needed here since
/// this only cares about one specific, well-known service name, not a full enumeration). All plain
/// registry/service reads, no shell-out - cheap enough to sit in the same start-up-plus-manual-
/// refresh tier as the #217-220 device-topology load.
///
/// NetworkThrottlingIndex in particular throttles non-multimedia network throughput to ~10
/// packets/ms whenever a multimedia app is active and is a common, invisible cause of "the network
/// gets slow while I'm gaming/watching video" reports.
/// </summary>
public static class MmcssService
{
    private const string ProfilePath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private static readonly string[] TaskNames = { "Audio", "Games", "Pro Audio" };

    public static MmcssAuditInfo Read()
    {
        bool running = false;
        string serviceStatusText = "Unknown (service not found)";
        try
        {
            using var sc = new ServiceController("MMCSS");
            var status = sc.Status; // throws if the service doesn't exist on this Windows build
            serviceStatusText = status.ToString();
            running = status == ServiceControllerStatus.Running;
        }
        catch
        {
            // Service not present, or access denied - stays "Unknown (service not found)" above.
        }

        int? systemResponsiveness = ReadDword(ProfilePath, "SystemResponsiveness");
        int? networkThrottlingIndex = ReadDword(ProfilePath, "NetworkThrottlingIndex");

        var profiles = new List<MmcssTaskProfileRow>();
        foreach (var task in TaskNames)
        {
            string taskPath = $@"{ProfilePath}\Tasks\{task}";
            profiles.Add(new MmcssTaskProfileRow
            {
                TaskName = task,
                GpuPriority = ReadString(taskPath, "GPU Priority") ?? "Unknown",
                Priority = ReadString(taskPath, "Priority") ?? "Unknown",
                SchedulingCategory = ReadString(taskPath, "Scheduling Category") ?? "Unknown",
                SfioPriority = ReadString(taskPath, "SFIO Priority") ?? "Unknown",
            });
        }

        return new MmcssAuditInfo
        {
            ServiceRunning = running,
            ServiceStatusText = serviceStatusText,
            SystemResponsiveness = systemResponsiveness,
            NetworkThrottlingIndex = networkThrottlingIndex,
            TaskProfiles = profiles,
            StatusText = running
                ? "MMCSS is running — multimedia scheduling boosts (GPU/CPU/SFIO priority bumps for registered tasks) are available."
                : "MMCSS is not running — multimedia scheduling boosts won't apply, which can present as audio glitches/dropped frames under load.",
        };
    }

    private static int? ReadDword(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            object? v = key?.GetValue(name);
            return v is null ? null : Convert.ToInt32(v);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name)?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
