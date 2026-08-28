using System.ServiceProcess;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #284-293: the cheap, always-on half of the Responsiveness tab's "Background-activity ribbon" -
/// SysMain service+registry state (#288), Delivery Optimization service+policy state (#289's cheap
/// part), cloud-sync/game-download client process cost (#293), and Storage Sense's configured
/// state (#293). Every read here is either a single named-service ServiceController query (the same
/// tier MmcssService.Read already established for one specific service) or a plain registry/already-
/// polled-process read - genuinely cheap enough to ride ResponsivenessViewModel's 2s _lightTimer,
/// per CLAUDE.md's on-demand-vs-polled rule. The heavier event-log-scan pieces for #285/#286/#287/
/// #289/#290 live in their own dedicated services instead (DefenderActivityService,
/// SearchIndexerActivityService, DeliveryOptimizationService, WindowsUpdateActivityService).
/// </summary>
public static class BackgroundActivityService
{
    // #293: well-known cloud-sync client executables - loose proxies by process name, not a
    // definitive "is currently syncing" API (none of these vendors expose one to third-party apps).
    public static readonly string[] CloudSyncProcessNames = { "OneDrive", "Dropbox", "GoogleDriveFS" };

    // #293: Steam/Epic are presence+cost proxies only - exact download-vs-idle detection isn't
    // cheaply available for either client, so this can only say "the client's background helper is
    // running, at this CPU/disk cost", not "a download is in progress right now". Documented in the
    // UI text too (see ResponsivenessView.xaml's Game Downloads card).
    public static readonly string[] GameClientProcessNames = { "steamwebhelper", "EpicGamesLauncher" };

    // #290: Windows Update/servicing worker processes - none of these running is the normal case.
    public static readonly string[] WindowsUpdateProcessNames = { "TrustedInstaller", "TiWorker", "MoUsoCoreWorker", "CompatTelRunner" };

    // #287: Search indexer's own worker processes.
    public static readonly string[] SearchIndexerProcessNames = { "SearchIndexer", "SearchProtocolHost", "SearchFilterHost" };

    /// <summary>#288: SysMain service state + prefetch/superfetch registry configuration - the
    /// simpler of the two options the item allows (see SysMainInfo's remarks on why per-service-
    /// inside-svchost I/O attribution was skipped).</summary>
    public static SysMainInfo ReadSysMain()
    {
        bool running = false;
        string statusText = "Unknown (service not found)";
        try
        {
            using var sc = new ServiceController("SysMain");
            var status = sc.Status; // throws if the service doesn't exist on this Windows build
            statusText = status.ToString();
            running = status == ServiceControllerStatus.Running;
        }
        catch
        {
            // Service not present (or access denied) - stays "Unknown (service not found)" above.
        }

        bool? prefetcher = ReadDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnablePrefetcher") is { } p ? p != 0 : null;
        bool? superfetch = ReadDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnableSuperfetch") is { } s ? s != 0 : null;

        string status2 = running
            ? "SysMain is running - prefetching/superfetch data is being maintained in the background."
            : "SysMain is not running - no prefetch/superfetch background activity from this service.";

        return new SysMainInfo
        {
            ServiceRunning = running,
            ServiceStatusText = statusText,
            PrefetcherEnabled = prefetcher,
            SuperfetchEnabled = superfetch,
            StatusText = status2,
        };
    }

    /// <summary>#289 cheap part: Delivery Optimization service state + the DODownloadMode peer-
    /// caching policy. The on-demand event-log activity read lives in
    /// DeliveryOptimizationService.ReadRecentActivityAsync instead.</summary>
    public static DeliveryOptimizationInfo ReadDeliveryOptimization()
    {
        bool running = false;
        string statusText = "Unknown (service not found)";
        try
        {
            using var sc = new ServiceController("DoSvc");
            var status = sc.Status;
            statusText = status.ToString();
            running = status == ServiceControllerStatus.Running;
        }
        catch
        {
            // Service not present (or access denied).
        }

        int? mode = ReadDword(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode");
        string modeText = DescribeDownloadMode(mode);

        return new DeliveryOptimizationInfo
        {
            ServiceRunning = running,
            ServiceStatusText = statusText,
            DownloadMode = mode,
            DownloadModeText = modeText,
            StatusText = running
                ? $"Delivery Optimization service is running (mode: {modeText})."
                : "Delivery Optimization service is not running.",
        };
    }

    private static string DescribeDownloadMode(int? mode) => mode switch
    {
        null => "Not configured (Windows default: HTTP + peers on the same NAT)",
        0 => "HTTP only (no peer sharing)",
        1 => "LAN peers",
        2 => "Group peers",
        3 => "Internet peers",
        99 => "Simple (HTTP only, no peering, no cloud service use)",
        100 => "Bypass (use Windows Update Delivery Optimization defaults)",
        _ => $"Unknown mode ({mode})",
    };

    /// <summary>#290 cheap part: wuauserv service state + already-polled process cost for the
    /// Windows Update/servicing worker executables.</summary>
    public static WindowsUpdateActivityInfo ReadWindowsUpdateProcessState(IReadOnlyList<ProcessRow> processes)
    {
        bool running = false;
        string statusText = "Unknown (service not found)";
        try
        {
            using var sc = new ServiceController("wuauserv");
            var status = sc.Status;
            statusText = status.ToString();
            running = status == ServiceControllerStatus.Running;
        }
        catch
        {
            // Service not present (or access denied).
        }

        return new WindowsUpdateActivityInfo
        {
            ServiceRunning = running,
            ServiceStatusText = statusText,
            ActiveProcesses = MatchProcesses(processes, WindowsUpdateProcessNames),
        };
    }

    /// <summary>#287 cheap part: Search indexer's own worker-process cost.</summary>
    public static SearchIndexerLiveInfo ReadSearchIndexerLiveState(IReadOnlyList<ProcessRow> processes)
    {
        var matches = MatchProcesses(processes, SearchIndexerProcessNames);
        return new SearchIndexerLiveInfo
        {
            AnyProcessRunning = matches.Count > 0,
            TotalCpuPercent = matches.Sum(m => m.CpuPercent),
            TotalDiskBytesPerSec = matches.Sum(m => m.DiskBytesPerSec),
        };
    }

    /// <summary>#293: cloud-sync/game-client process cost, matched by process name against the
    /// already-polled process list - no new per-process syscall needed here.</summary>
    public static List<ProcessCostRow> MatchProcesses(IReadOnlyList<ProcessRow> processes, string[] names) =>
        processes
            .Where(p => names.Any(n => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new ProcessCostRow { ProcessName = p.Name, Pid = p.Pid, CpuPercent = p.CpuPercent, DiskBytesPerSec = p.DiskBytesPerSec })
            .ToList();

    /// <summary>#293: Storage Sense's configured enable/frequency state - see StorageSenseInfo's
    /// remarks on why the underlying value names (01/2048) are an undocumented-but-community-
    /// confirmed convention, and why "running right now" isn't reported at all.</summary>
    public static StorageSenseInfo ReadStorageSense()
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            if (key is null)
            {
                return new StorageSenseInfo
                {
                    KeyPresent = false,
                    Enabled = null,
                    RunFrequencyText = "Unknown",
                    StatusText = "No Storage Sense configuration found under HKCU - its on/off state then follows this Windows edition's own default, which this registry key alone doesn't reveal.",
                };
            }

            int? enabledRaw = key.GetValue("01") is { } e ? System.Convert.ToInt32(e) : (int?)null;
            int? freqRaw = key.GetValue("2048") is { } f ? System.Convert.ToInt32(f) : (int?)null;
            bool? enabled = enabledRaw is { } ev ? ev != 0 : null;
            string freqText = freqRaw switch
            {
                null => "Unknown",
                0 => "When Windows decides storage is low",
                1 => "Every day",
                7 => "Every week",
                30 => "Every month",
                var v => $"Every {v} days",
            };

            return new StorageSenseInfo
            {
                KeyPresent = true,
                Enabled = enabled,
                RunFrequencyText = freqText,
                StatusText = enabled switch
                {
                    true => $"Storage Sense is enabled (run frequency: {freqText}). Live \"running right now\" detection isn't cheaply available - no documented API/event reports an in-progress cleanup pass.",
                    false => "Storage Sense is disabled.",
                    null => "Storage Sense configuration key is present, but its enable flag couldn't be read.",
                },
            };
        }
        catch
        {
            return new StorageSenseInfo
            {
                KeyPresent = false,
                Enabled = null,
                RunFrequencyText = "Unknown",
                StatusText = "Couldn't read Storage Sense configuration (access denied or unavailable).",
            };
        }
    }

    private static int? ReadDword(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            object? v = key?.GetValue(name);
            return v is null ? null : System.Convert.ToInt32(v);
        }
        catch
        {
            return null;
        }
    }
}
