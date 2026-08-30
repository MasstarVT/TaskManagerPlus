using System.Diagnostics;
using System.IO;
using System.Management;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #734-737: Fast Startup (HiberbootEnabled) state and everything that reads or acts on it - the
/// uptime-clock reconciliation (#734), the "you haven't fully restarted in N days" prompt's
/// one-click full restart (#735), the hibernation/sleep-state inventory and its confirmed toggles
/// (#736), and the Fast-Startup side-effect flags plus its own confirmed "turn it off" action
/// (#737). Implemented as one service, matching how tightly these four items are coupled in
/// practice (the same HiberbootEnabled read feeds three separate UI cards). Shells out to
/// powercfg.exe for everything powercfg itself exposes (sleep-state availability, the hibernation
/// on/off/type/size toggles - CLAUDE.md's "prefer a known Windows tool over raw interop"), and
/// reads/writes the documented HiberbootEnabled registry value directly for the one thing powercfg
/// has no switch for (Fast Startup itself is a Control Panel checkbox, not a powercfg feature -
/// this reads/writes the exact same registry location that checkbox does, the same "the documented
/// value Explorer/Control Panel itself uses" tradeoff StartupManagerService's StartupApproved flag
/// already takes).
/// </summary>
public static class FastStartupService
{
    private const string PowerKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    private const string HiberFilePath = @"C:\hiberfil.sys";

    #region #734: uptime reconciliation

    /// <summary>#734: reconciles Environment.TickCount64 (resets every Fast Startup hybrid
    /// shutdown), Win32_OperatingSystem.LastBootUpTime (WMI's own opinion, tracks TickCount64
    /// closely), and the last full/cold boot (Kernel-Boot event 27, boot type 0 - reused from
    /// BootPerformanceService.ReadLastFullBootTime rather than re-querying the channel).</summary>
    public static FastStartupInfo ReadUptimeInfo()
    {
        bool? hiberboot = ReadHiberbootEnabled();
        var tickUptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        DateTime? lastBootWmi = ReadLastBootUpTimeWmi();
        DateTime? lastFullBoot = BootPerformanceService.ReadLastFullBootTime();
        TimeSpan? sinceFull = lastFullBoot is { } lf ? DateTime.Now - lf : null;

        return new FastStartupInfo
        {
            HiberbootEnabled = hiberboot,
            TickCountUptime = tickUptime,
            LastBootUpTimeWmi = lastBootWmi,
            LastFullBootTime = lastFullBoot,
            SinceLastFullRestart = sinceFull,
        };
    }

    public static bool? ReadHiberbootEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PowerKeyPath);
            return key?.GetValue("HiberbootEnabled") switch
            {
                int i => i != 0,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? ReadLastBootUpTimeWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                if (mo["LastBootUpTime"] is string wmiDate)
                    return ManagementDateTimeConverter.ToDateTime(wmiDate);
            }
        }
        catch
        {
            // WMI unavailable - degrade to Unknown, same as every other WMI read in this app.
        }
        return null;
    }

    #endregion

    #region #735: full-restart prompt action

    /// <summary>#735: `shutdown /g /f /t 0` - forces a genuine full boot (bypassing Fast Startup's
    /// hybrid resume entirely), unlike the regular Shut Down/Restart path under Fast Startup. Only
    /// ever called after the caller has shown this exact command in a confirmation dialog (see
    /// StartupViewModel.FullRestartAsync). Fire-and-forget: a successful call tears down this
    /// process along with everything else, so there is nothing meaningful to await.</summary>
    public static (bool Success, string? Error) TriggerFullRestart()
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/g /f /t 0") { UseShellExecute = false, CreateNoWindow = true });
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    #endregion

    #region #736: hibernation / sleep-state inventory

    /// <summary>#736: parses `powercfg /a`'s "available"/"not available" sleep-state report -
    /// same adaptive text-block parse tradeoff as BcdInspectorService's bcdedit parse (powercfg's
    /// exact wording isn't a versioned schema, so this reads section headers/reasons by shape
    /// rather than a fixed line index).</summary>
    public static async Task<List<SleepStateInfo>> ReadSleepStatesAsync()
    {
        var result = new List<SleepStateInfo>();
        try
        {
            string output = (await RunCapturedAsync("powercfg.exe", "/a")).Output;
            var lines = output.Replace("\r\n", "\n").Split('\n');

            bool? currentlyAvailable = null;
            string? pendingName = null;
            foreach (var raw in lines)
            {
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;

                if (line.Contains("following sleep states are available", StringComparison.OrdinalIgnoreCase)) { currentlyAvailable = true; pendingName = null; continue; }
                if (line.Contains("following sleep states are not available", StringComparison.OrdinalIgnoreCase)) { currentlyAvailable = false; pendingName = null; continue; }
                if (currentlyAvailable is null) continue; // preamble before the first section header

                bool indented = raw.StartsWith(" ") || raw.StartsWith("\t");
                string trimmed = line.Trim();

                if (!indented) { currentlyAvailable = null; continue; } // an unindented line ends both list sections

                // Within an "available" section every indented line is a state name. Within a
                // "not available" section, a lightly-indented line is the state name and a more
                // deeply-indented line under it is powercfg's own reason text.
                int indent = raw.Length - raw.TrimStart(' ', '\t').Length;
                if (currentlyAvailable == true)
                {
                    result.Add(new SleepStateInfo { Name = trimmed, Available = true });
                }
                else if (indent <= 4)
                {
                    pendingName = trimmed;
                    result.Add(new SleepStateInfo { Name = trimmed, Available = false, UnavailableReason = null });
                }
                else if (pendingName is not null)
                {
                    var existing = result.LastOrDefault(s => s.Name == pendingName && !s.Available);
                    if (existing is not null)
                    {
                        int idx = result.LastIndexOf(existing);
                        result[idx] = new SleepStateInfo { Name = existing.Name, Available = false, UnavailableReason = trimmed };
                    }
                }
            }
        }
        catch
        {
            // powercfg missing/blocked - an empty list, same degrade-to-nothing pattern as
            // PowerPlanService.ListPowerPlansAsync.
        }
        return result;
    }

    /// <summary>#736: the real, measured C:\hiberfil.sys size on disk, compared against installed
    /// RAM - see HiberFileInfo's remarks for why this measures the actual file rather than trying
    /// to read a configured-percentage value powercfg has no query switch for.</summary>
    public static HiberFileInfo ReadHiberFileInfo()
    {
        long size = 0;
        bool exists = false;
        try
        {
            var info = new FileInfo(HiberFilePath);
            exists = info.Exists;
            if (exists) size = info.Length;
        }
        catch
        {
            // Access denied/path unavailable - degrade to "not found" rather than guessing.
        }

        var (_, totalRam) = BcdInspectorService.ReadSystemTotals();
        return new HiberFileInfo { FileExists = exists, HiberFileSizeBytes = size, TotalRamBytes = totalRam };
    }

    public static async Task<(bool Success, string? Error)> SetHibernateEnabledAsync(bool enabled)
    {
        var (output, exitCode) = await RunCapturedAsync("powercfg.exe", enabled ? "/hibernate on" : "/hibernate off");
        return exitCode == 0 ? (true, null) : (false, ErrorText(output));
    }

    public static async Task<(bool Success, string? Error)> SetHiberFileTypeReducedAsync()
    {
        var (output, exitCode) = await RunCapturedAsync("powercfg.exe", "/hibernate /type reduced");
        return exitCode == 0 ? (true, null) : (false, ErrorText(output));
    }

    public static async Task<(bool Success, string? Error)> SetHiberFileSizeAsync(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        var (output, exitCode) = await RunCapturedAsync("powercfg.exe", $"/hibernate /size {percent}");
        return exitCode == 0 ? (true, null) : (false, ErrorText(output));
    }

    #endregion

    #region #737: Fast Startup side-effect flags + disable action

    /// <summary>#737: the documented consequences of leaving Fast Startup on - informational only,
    /// shown whenever HiberbootEnabled is 1 (see StartupViewModel).</summary>
    public static List<FastStartupSideEffect> SideEffects { get; } = new()
    {
        new FastStartupSideEffect
        {
            Title = "Dual-booted Linux can't mount the Windows (NTFS) partitions",
            Detail = "Windows leaves its NTFS volumes in a \"hibernated\" locked state so the kernel session can resume from them - Linux refuses to mount them read-write (sometimes at all) until Windows does a full shutdown.",
        },
        new FastStartupSideEffect
        {
            Title = "Driver and firmware updates can appear not to apply",
            Detail = "A driver or BIOS/firmware update that needs a real re-initialization pass (not just a resumed kernel session) can look like it silently failed to take effect until the next full boot.",
        },
        new FastStartupSideEffect
        {
            Title = "External drives aren't always cleanly released",
            Detail = "A drive that was in use when the hybrid shutdown happened can stay logically \"in use\" from Windows' perspective across the resume, which can surface as unexpected access errors or a drive that won't safely eject.",
        },
        new FastStartupSideEffect
        {
            Title = "A queued chkdsk or a BIOS setting change can appear ignored",
            Detail = "Anything that's supposed to run once \"at the next boot\" (a scheduled chkdsk, some BIOS/UEFI setting changes) needs a real full boot to actually execute - a hybrid resume skips right past it.",
        },
    };

    /// <summary>#737: sets HiberbootEnabled to 0 - the same registry value Control Panel's "Turn
    /// on fast startup" checkbox itself writes (see this class's remarks). Only ever called after
    /// the caller has shown this in a confirmation dialog.</summary>
    public static (bool Success, string? Error) DisableFastStartup()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PowerKeyPath, writable: true);
            if (key is null) return (false, "Couldn't open the Power registry key (needs Administrator).");
            int? previous = key.GetValue("HiberbootEnabled") as int?;
            key.SetValue("HiberbootEnabled", 0, RegistryValueKind.DWord);

            // #796: journal this write - see RegistryChangeJournalService's remarks for the rest
            // of this chunk's (deliberately partial) registry-write coverage.
            RegistryChangeJournalService.Record(
                source: "Fast Startup",
                description: "Turned off Fast Startup (HiberbootEnabled)",
                hive: "HKLM",
                subKeyPath: PowerKeyPath,
                valueName: "HiberbootEnabled",
                kind: RegistryValueKind.DWord,
                oldValueText: previous?.ToString(),
                newValueText: "0");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    #endregion

    private static string ErrorText(string output) => string.IsNullOrWhiteSpace(output) ? "powercfg failed." : output.Trim();

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's historical default timeout.</summary>
    private static Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 10000)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
}
