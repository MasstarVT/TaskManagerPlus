using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 18, items 71-80: "dump configuration and capture health" - reads
/// HKLM\SYSTEM\CurrentControlSet\Control\CrashControl (item 71), cross-checks it against the
/// system's page-file configuration (items 72/73/74/75), Fast Startup (78), and
/// hibernation/BitLocker interactions (79), then folds all of it into a single pass/fail
/// checklist (item 80) - see CrashDumpConfiguration's own remarks for the full field list.
///
/// Follows the same conventions as the round-17 crash-capture services already on this tab:
/// registry reads degrade to null/"Unknown" rather than throwing (ForcedCrashService,
/// MinidumpHousekeepingService), WMI reads degrade to an empty result (SystemSpecsService's
/// ReadPageFileLocation), and the two external tools this needs (powercfg/manage-bde) are
/// shelled out to and their text output parsed rather than reimplemented via COM/undocumented
/// APIs (CLAUDE.md's "prefer a known Windows tool" convention - PowerPlanService already does
/// exactly this for powercfg /a, just for a different report section).
/// </summary>
public static class CrashDumpConfigService
{
    private const string CrashControlKeyPath = @"SYSTEM\CurrentControlSet\Control\CrashControl";
    private const string PowerKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";

    public static async Task<CrashDumpConfiguration> ReadConfigurationAsync()
    {
        var core = await Task.Run(ReadCore);

        var (hiberText, bitlockerText) = await ReadHibernationAndBitLockerAsync(core.DumpTargetVolume);

        return core with
        {
            HibernationStatusText = hiberText,
            DumpVolumeBitLockerStatusText = bitlockerText,
        };
    }

    /// <summary>Everything readable synchronously (registry + WMI + DriveInfo) - items 71/72/73/
    /// 74/75/76/78. Split out from ReadConfigurationAsync so it can run under one Task.Run.</summary>
    private static CrashDumpConfiguration ReadCore()
    {
        int? rawType = ReadDwordAsInt(CrashControlKeyPath, "CrashDumpEnabled");
        string dumpTypeText = DescribeDumpType(rawType);
        bool needsPageFile = rawType is 1 or 2 or 3 or 7; // Complete/Kernel/Small/Automatic all need one; None (0) doesn't.

        string? dumpFile = ExpandEnvironment(ReadString(CrashControlKeyPath, "DumpFile"));
        string? minidumpDir = ExpandEnvironment(ReadString(CrashControlKeyPath, "MinidumpDir"));
        bool? overwrite = ReadDwordAsBool(CrashControlKeyPath, "Overwrite");
        bool? autoReboot = ReadDwordAsBool(CrashControlKeyPath, "AutoReboot");
        bool? logEvent = ReadDwordAsBool(CrashControlKeyPath, "LogEvent");
        bool? alwaysKeep = ReadDwordAsBool(CrashControlKeyPath, "AlwaysKeepMemoryDump");
        string? dedicated = ExpandEnvironment(ReadString(CrashControlKeyPath, "DedicatedDumpFile"));
        int? dumpFileSizeMb = ReadDwordAsInt(CrashControlKeyPath, "DumpFileSize");
        int? minidumpsCount = ReadDwordAsInt(CrashControlKeyPath, "MinidumpsCount");
        bool? hiberboot = ReadDwordAsBool(PowerKeyPath, "HiberbootEnabled");

        var (pageFiles, ramBytes) = ReadPageFileInfo();
        bool pageFileDisabled = pageFiles.Count == 0;

        // Item 72: rough required sizes - Microsoft doesn't publish an exact formula, so these are
        // documented approximations ("worth a manual check," not a guarantee - CLAUDE.md's "quick
        // flag, not a verdict"). Complete dump: all of physical RAM plus a small header/overhead
        // margin. Kernel/Automatic/Active dump: only kernel-mode pages, commonly well under total
        // RAM, but with no fixed ratio Windows guarantees - a conservative third of RAM (floor
        // 800 MB) is used as the "probably enough" bar.
        long completeReq = ramBytes > 0 ? ramBytes + 257L * 1024 * 1024 : 0;
        long kernelReq = ramBytes > 0 ? Math.Max(800L * 1024 * 1024, ramBytes / 3) : 0;
        long requiredForType = rawType switch
        {
            1 => completeReq,
            2 or 3 or 7 => kernelReq,
            _ => 0,
        };

        bool pageFileOnSystemVolume = pageFiles.Any(p => p.IsSystemVolume);
        long systemVolumeCapacityMb = pageFiles.Where(p => p.IsSystemVolume)
            .Sum(p => p.IsSystemManaged ? (p.AllocatedSizeMb ?? 0) : Math.Max(p.MaximumSizeMb, p.AllocatedSizeMb ?? 0));
        bool sysVolSufficient = requiredForType <= 0 || (systemVolumeCapacityMb * 1024L * 1024L) >= requiredForType;

        // Items 74/75: whichever path the next crash would actually try to write to.
        string? targetPath = !string.IsNullOrWhiteSpace(dedicated) ? dedicated : dumpFile;
        string? targetVolume = ExtractVolume(targetPath);
        long? freeBytes = ReadFreeSpace(targetVolume);
        var (healthLevel, healthText) = ClassifyDumpTargetHealth(freeBytes, requiredForType);

        var fields = BuildFields(rawType, dumpTypeText, dumpFile, minidumpDir, overwrite, autoReboot, logEvent, alwaysKeep, dedicated, dumpFileSizeMb);

        return new CrashDumpConfiguration
        {
            CrashDumpEnabledRaw = rawType,
            DumpTypeText = dumpTypeText,
            DumpTypeNeedsPageFile = needsPageFile,
            DumpFile = dumpFile,
            MinidumpDir = minidumpDir,
            Overwrite = overwrite,
            AutoReboot = autoReboot,
            LogEvent = logEvent,
            AlwaysKeepMemoryDump = alwaysKeep,
            DedicatedDumpFile = dedicated,
            DumpFileSizeMb = dumpFileSizeMb,
            MinidumpsCount = minidumpsCount,
            Fields = fields,

            TotalRamBytes = ramBytes,
            PageFiles = pageFiles,
            PageFileDisabled = pageFileDisabled,
            RequiredSizeForCompleteBytes = completeReq,
            RequiredSizeForKernelBytes = kernelReq,
            RequiredSizeForConfiguredTypeBytes = requiredForType,
            PageFileOnSystemVolume = pageFileOnSystemVolume,
            SystemVolumePageFileSufficient = sysVolSufficient,

            DumpTargetPath = targetPath,
            DumpTargetVolume = targetVolume,
            DumpTargetFreeBytes = freeBytes,
            DumpTargetHealthLevel = healthLevel,
            DumpTargetHealthText = healthText,

            HiberbootEnabled = hiberboot,
        };
    }

    private static List<CrashDumpConfigField> BuildFields(
        int? rawType, string dumpTypeText, string? dumpFile, string? minidumpDir, bool? overwrite,
        bool? autoReboot, bool? logEvent, bool? alwaysKeep, string? dedicated, int? dumpFileSizeMb)
    {
        static string YesNo(bool? v) => v switch { true => "Yes", false => "No", null => "Not set (Windows default applies)" };

        var fields = new List<CrashDumpConfigField>
        {
            new("Dump type (CrashDumpEnabled)", rawType is null ? "Not set (Unknown)" : $"{dumpTypeText} ({rawType})"),
            new("Dump file (DumpFile)", string.IsNullOrWhiteSpace(dumpFile) ? "Not set" : dumpFile!),
            new("Minidump folder (MinidumpDir)", string.IsNullOrWhiteSpace(minidumpDir) ? "Not set (default %SystemRoot%\\Minidump)" : minidumpDir!),
            new("Overwrite existing dump file", YesNo(overwrite)),
            new("Automatically restart after a crash (AutoReboot)", YesNo(autoReboot)),
            new("Write an event-log entry on crash (LogEvent)", YesNo(logEvent)),
            new("Keep the dump file even when low on disk space (AlwaysKeepMemoryDump)", YesNo(alwaysKeep)),
        };

        fields.Add(!string.IsNullOrWhiteSpace(dedicated)
            ? new CrashDumpConfigField("Dedicated dump file", $"{dedicated} (max {(dumpFileSizeMb is { } mb and > 0 ? $"{mb} MB" : "not limited")})")
            : new CrashDumpConfigField("Dedicated dump file", "Not set - the dump is written to the system page file's volume"));

        return fields;
    }

    /// <summary>Item 71's CrashDumpEnabled -&gt; plain English, per Microsoft's documented values.
    /// 7 (Automatic) is the Windows 8/Server 2012 R2+ default; 1 with a companion FilterPages
    /// value is "Active memory dump" (Windows 10 1607+) - this app doesn't attempt that further
    /// distinction since FilterPages isn't otherwise surfaced anywhere on this tab.</summary>
    private static string DescribeDumpType(int? raw) => raw switch
    {
        0 => "None - no dump is written on a bugcheck",
        1 => "Complete memory dump",
        2 => "Kernel memory dump",
        3 => "Small memory dump (256 KB minidump)",
        7 => "Automatic memory dump",
        null => "Not set",
        _ => $"Unrecognized value ({raw})",
    };

    /// <summary>Items 72/73: Win32_PageFileSetting (configured page files) joined to
    /// Win32_PageFileUsage (their allocated size) by path - the same
    /// ManagementObjectSearcher-per-class-then-merge shape SystemSpecsService.ReadPageFileLocation
    /// already uses for a single page file; this reads every configured one, since a system can
    /// have more than one. Returns an empty list (not a guess) when the query itself fails, and
    /// separately when Windows genuinely has no page file configured at all (item 73) - both look
    /// the same to a caller that only wants "how much page file is there", which is exactly what
    /// item 73 needs to flag.</summary>
    private static (List<PageFileConfigEntry> PageFiles, long RamBytes) ReadPageFileInfo()
    {
        var entries = new List<PageFileConfigEntry>();
        string systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');

        try
        {
            var usageByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using (var usageSearcher = new ManagementObjectSearcher("SELECT Name, AllocatedBaseSize FROM Win32_PageFileUsage"))
            {
                foreach (ManagementObject mo in usageSearcher.Get())
                {
                    string name = (mo["Name"] as string ?? string.Empty).Trim();
                    if (name.Length == 0) continue;
                    usageByPath[name] = Convert.ToInt64(mo["AllocatedBaseSize"] ?? 0L);
                }
            }

            using (var settingSearcher = new ManagementObjectSearcher("SELECT Name, InitialSize, MaximumSize FROM Win32_PageFileSetting"))
            {
                foreach (ManagementObject mo in settingSearcher.Get())
                {
                    string name = (mo["Name"] as string ?? string.Empty).Trim();
                    if (name.Length < 2) continue;
                    long initial = Convert.ToInt64(mo["InitialSize"] ?? 0L);
                    long max = Convert.ToInt64(mo["MaximumSize"] ?? 0L);
                    string volume = name.Substring(0, 2);

                    entries.Add(new PageFileConfigEntry
                    {
                        Path = name,
                        Volume = volume,
                        IsSystemVolume = volume.Equals(systemDrive, StringComparison.OrdinalIgnoreCase),
                        IsSystemManaged = initial == 0 && max == 0,
                        InitialSizeMb = initial,
                        MaximumSizeMb = max,
                        AllocatedSizeMb = usageByPath.TryGetValue(name, out var alloc) ? alloc : null,
                    });
                }
            }
        }
        catch
        {
            // WMI namespace/class unavailable - degrade to "no page file info", same as
            // SystemSpecsService.ReadPageFileLocation's own failure mode.
        }

        long ramBytes = 0;
        try
        {
            using var ramSearcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in ramSearcher.Get())
            {
                ramBytes = Convert.ToInt64(mo["TotalPhysicalMemory"] ?? 0L);
                break;
            }
        }
        catch { /* leave 0 - required-size fields degrade to 0 (unknown) above */ }

        return (entries, ramBytes);
    }

    /// <summary>Item 75: red/amber/green free-space read on whichever volume DumpTargetPath is
    /// on. DriveInfo, not WMI, matching SystemSpecsService.ReadVolumesAsync's own reasoning
    /// (handles an unready/removable drive cleanly).</summary>
    private static long? ReadFreeSpace(string? volume)
    {
        if (string.IsNullOrWhiteSpace(volume)) return null;
        try
        {
            var drive = new DriveInfo(volume);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }

    private static (DumpTargetHealth Level, string Text) ClassifyDumpTargetHealth(long? freeBytes, long requiredBytes)
    {
        if (freeBytes is null)
            return (DumpTargetHealth.Unknown, "Unknown - couldn't read free space on the dump target volume.");

        if (requiredBytes <= 0)
            return (DumpTargetHealth.Unknown, $"{Formatting.FormatBytes(freeBytes.Value)} free - dump type isn't configured, so a size requirement can't be computed.");

        double ratio = (double)freeBytes.Value / requiredBytes;
        if (ratio >= 1.2)
            return (DumpTargetHealth.Green, $"{Formatting.FormatBytes(freeBytes.Value)} free - comfortably above the ~{Formatting.FormatBytes(requiredBytes)} the configured dump type needs.");
        if (ratio >= 1.0)
            return (DumpTargetHealth.Amber, $"{Formatting.FormatBytes(freeBytes.Value)} free - only just enough for the ~{Formatting.FormatBytes(requiredBytes)} the configured dump type needs.");
        return (DumpTargetHealth.Red, $"{Formatting.FormatBytes(freeBytes.Value)} free - below the ~{Formatting.FormatBytes(requiredBytes)} the configured dump type needs. The next crash likely won't leave a dump.");
    }

    /// <summary>Item 79: `powercfg /a` (hibernation availability) and `manage-bde -status
    /// &lt;volume&gt;` (BitLocker on the dump target) - both informational-only text reads,
    /// degrading to "Unknown" rather than guessing, per CLAUDE.md.</summary>
    private static async Task<(string HibernationStatusText, string? BitLockerStatusText)> ReadHibernationAndBitLockerAsync(string? dumpTargetVolume)
    {
        string hibernationText = "Unknown";
        try
        {
            var (output, exitCode) = await RunCapturedAsync("powercfg.exe", "/a");
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                if (output.Contains("Hibernation has not been enabled", StringComparison.OrdinalIgnoreCase))
                    hibernationText = "Hibernation is disabled on this machine (no hiberfil.sys) - this doesn't affect crash dumps directly, but a Kernel/Automatic dump's size headroom can be shared with the hibernation file's own reservation on some configurations, so it's worth knowing about.";
                else if (Regex.IsMatch(output, @"^\s*Hibernate\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                    hibernationText = "Hibernation is enabled (hiberfil.sys exists).";
                else
                    hibernationText = "Unknown - powercfg /a didn't report hibernation in a recognizable way.";
            }
        }
        catch
        {
            // powercfg missing/blocked - leave "Unknown".
        }

        string? bitlockerText = null;
        if (!string.IsNullOrWhiteSpace(dumpTargetVolume))
        {
            bitlockerText = "Unknown";
            try
            {
                var (output, exitCode) = await RunCapturedAsync("manage-bde.exe", $"-status {dumpTargetVolume}");
                if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    var protectionMatch = Regex.Match(output, @"Protection Status:\s*(.+)", RegexOptions.IgnoreCase);
                    var lockMatch = Regex.Match(output, @"Lock Status:\s*(.+)", RegexOptions.IgnoreCase);
                    if (protectionMatch.Success)
                    {
                        string protection = protectionMatch.Groups[1].Value.Trim();
                        bool isOn = protection.Contains("Protection On", StringComparison.OrdinalIgnoreCase);
                        bitlockerText = isOn
                            ? $"BitLocker is on for {dumpTargetVolume} ({protection}" + (lockMatch.Success ? $", {lockMatch.Groups[1].Value.Trim()}" : string.Empty) + ") - an unlocked, decrypting-capable volume at boot time is needed for the dump to be written and later read."
                            : $"BitLocker is off for {dumpTargetVolume} ({protection}).";
                    }
                    else
                    {
                        bitlockerText = $"{dumpTargetVolume} doesn't look BitLocker-protected.";
                    }
                }
                else
                {
                    bitlockerText = "Unknown - manage-bde didn't return status for this volume (not present on this Windows edition, or access denied).";
                }
            }
            catch
            {
                bitlockerText = "Unknown - manage-bde.exe isn't available on this machine.";
            }
        }

        return (hibernationText, bitlockerText);
    }

    // ---------------------------------------------------------------------------------------
    // Item 80: "will this PC capture the next BSOD" checklist - a pure function over an already-
    // read CrashDumpConfiguration, so both RefreshAsync and (if ever needed) a re-evaluation
    // after a write action can call it without re-reading anything.
    // ---------------------------------------------------------------------------------------
    public static CrashCaptureChecklist BuildChecklist(CrashDumpConfiguration cfg)
    {
        var items = new List<CrashCaptureChecklistItem>();

        // Item 71.
        bool? typeOk = cfg.CrashDumpEnabledRaw is null ? null : cfg.CrashDumpEnabledRaw != 0;
        items.Add(new CrashCaptureChecklistItem
        {
            Label = "A dump type is configured",
            Passed = typeOk,
            Detail = cfg.CrashDumpEnabledRaw is null
                ? "Couldn't read CrashDumpEnabled."
                : typeOk == true ? $"{cfg.DumpTypeText}." : "CrashDumpEnabled is 0 (None) - Windows won't write any dump on a bugcheck.",
        });

        // Items 72/73/74: page file large enough on the system volume, or a dedicated dump file
        // compensating for it.
        bool dedicatedConfigured = !string.IsNullOrWhiteSpace(cfg.DedicatedDumpFile);
        bool dedicatedLooksAdequate = dedicatedConfigured && cfg.DumpTargetHealthLevel != DumpTargetHealth.Red;
        bool pageFileGateOk = !cfg.DumpTypeNeedsPageFile || cfg.SystemVolumePageFileSufficient || dedicatedLooksAdequate;
        string pageFileDetail;
        if (!cfg.DumpTypeNeedsPageFile)
            pageFileDetail = "Not applicable - the configured dump type doesn't need a page file.";
        else if (cfg.PageFileDisabled)
            pageFileDetail = $"No page file is configured at all - crash dumps can't be written. The configured dump type needs roughly {Formatting.FormatBytes(cfg.RequiredSizeForConfiguredTypeBytes)}.";
        else if (cfg.SystemVolumePageFileSufficient)
            pageFileDetail = "The system-volume page file is large enough for the configured dump type.";
        else if (dedicatedLooksAdequate)
            pageFileDetail = "The system-volume page file is too small, but a dedicated dump file is configured to compensate.";
        else
            pageFileDetail = $"The system-volume page file is too small for the configured dump type (needs roughly {Formatting.FormatBytes(cfg.RequiredSizeForConfiguredTypeBytes)}), and no dedicated dump file compensates for it.";
        items.Add(new CrashCaptureChecklistItem { Label = "Page file (or a dedicated dump file) is large enough", Passed = pageFileGateOk, Detail = pageFileDetail });

        // Item 75.
        bool? freeSpaceOk = cfg.DumpTargetHealthLevel switch
        {
            DumpTargetHealth.Green => true,
            DumpTargetHealth.Amber => true,
            DumpTargetHealth.Red => false,
            _ => (bool?)null,
        };
        items.Add(new CrashCaptureChecklistItem { Label = "Enough free space on the dump target volume", Passed = freeSpaceOk, Detail = cfg.DumpTargetHealthText });

        // Item 76 - informational (doesn't block capture, affects whether the stop code is seen).
        items.Add(new CrashCaptureChecklistItem
        {
            Label = "Stop code will be visible before reboot",
            Passed = cfg.AutoReboot != true,
            Detail = cfg.AutoReboot switch
            {
                true => "AutoReboot is on - the machine restarts immediately, before the stop code/QR code can be read. Turning it off is temporary diagnostic practice, not a permanent recommendation.",
                false => "AutoReboot is off - the stop-code screen stays up until manually restarted.",
                null => "AutoReboot isn't set (Windows default applies).",
            },
            AffectsCapture = false,
        });

        // Item 78 - informational (affects whether "did rebooting fix it" reasoning is valid).
        items.Add(new CrashCaptureChecklistItem
        {
            Label = "\"Did rebooting fix it\" reasoning is valid",
            Passed = cfg.HiberbootEnabled != true,
            Detail = cfg.HiberbootEnabled switch
            {
                true => "Fast Startup is on - a normal \"shut down\" is really a hibernate, so driver state can persist across a reboot that should have cleared it.",
                false => "Fast Startup is off - a shutdown fully clears driver state.",
                null => "Couldn't read HiberbootEnabled.",
            },
            AffectsCapture = false,
        });

        // Item 79 - informational.
        items.Add(new CrashCaptureChecklistItem
        {
            Label = "Hibernation / BitLocker interactions",
            Passed = null,
            Detail = cfg.HibernationStatusText + (cfg.DumpVolumeBitLockerStatusText is { } bl ? " " + bl : string.Empty),
            AffectsCapture = false,
        });

        var gating = items.Where(i => i.AffectsCapture).ToList();
        CrashCaptureVerdict verdict;
        string verdictText;
        if (gating.Any(i => i.Passed == false))
        {
            verdict = CrashCaptureVerdict.Fail;
            var reasons = gating.Where(i => i.Passed == false).Select(i => i.Detail);
            verdictText = "No - this PC is not configured to capture the next crash: " + string.Join(" ", reasons);
        }
        else if (gating.Any(i => i.Passed is null))
        {
            verdict = CrashCaptureVerdict.Uncertain;
            verdictText = "Uncertain - some of this couldn't be fully verified. Check the rows below.";
        }
        else
        {
            verdict = CrashCaptureVerdict.Pass;
            verdictText = "Yes - this PC looks configured to capture the next BSOD as a readable dump.";
        }

        return new CrashCaptureChecklist { Items = items, Verdict = verdict, VerdictText = verdictText };
    }

    // ---------------------------------------------------------------------------------------
    // Write actions - items 74/76/77/78. Every one of these is a real, consequential registry/
    // WMI write (page file sizing, reboot behavior, hibernation), so per this chunk's own
    // instructions none of them are called except from an explicit button the ViewModel gates
    // behind a MessageBox confirmation first (the same pattern EnableCrashOnCtrlScrollCommand/
    // PurgeWerReportsCommand already use elsewhere on this tab) - this service only ever
    // performs the write once asked to.
    // ---------------------------------------------------------------------------------------

    /// <summary>Item 76.</summary>
    public static bool SetAutoReboot(bool enable) => WriteDword(CrashControlKeyPath, "AutoReboot", enable ? 1 : 0);

    /// <summary>Item 78.</summary>
    public static bool SetHiberbootEnabled(bool enable) => WriteDword(PowerKeyPath, "HiberbootEnabled", enable ? 1 : 0);

    /// <summary>Item 74: points the dump at a different (larger) volume than the system page
    /// file's own, without touching the page file itself. sizeMb &lt;= 0 leaves DumpFileSize
    /// unset (Windows then sizes it to match physical RAM).</summary>
    public static bool WriteDedicatedDumpFile(string path, int sizeMb)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        bool ok = WriteString(CrashControlKeyPath, "DedicatedDumpFile", path.Trim());
        if (sizeMb > 0) ok &= WriteDword(CrashControlKeyPath, "DumpFileSize", sizeMb);
        else ok &= DeleteValue(CrashControlKeyPath, "DumpFileSize");
        return ok;
    }

    public static bool ClearDedicatedDumpFile()
    {
        bool ok1 = DeleteValue(CrashControlKeyPath, "DedicatedDumpFile");
        bool ok2 = DeleteValue(CrashControlKeyPath, "DumpFileSize");
        return ok1 && ok2;
    }

    /// <summary>Item 77: sets the dump type to Automatic (Windows' own recommended default since
    /// 8/Server 2012 R2), a sane MinidumpsCount, and switches the page file to system-managed so
    /// Windows sizes it itself - the same three things a manual "make this capture crashes
    /// properly" walkthrough would ask for. Returns which of the applied settings need a restart
    /// to actually take effect, so the UI can say so rather than implying everything is already
    /// live.</summary>
    public static (bool Ok, List<string> RestartNotes) ApplyRecommendedConfiguration()
    {
        var notes = new List<string>();
        bool ok = true;

        ok &= WriteDword(CrashControlKeyPath, "CrashDumpEnabled", 7);
        notes.Add("Dump type set to Automatic memory dump - takes effect on the next crash, no restart needed.");

        ok &= WriteDword(CrashControlKeyPath, "MinidumpsCount", 10);
        notes.Add("MinidumpsCount set to 10 - takes effect immediately.");

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
            bool wrote = false;
            foreach (ManagementObject mo in searcher.Get())
            {
                mo["AutomaticManagedPagefile"] = true;
                mo.Put();
                wrote = true;
                break;
            }
            if (wrote)
                notes.Add("Page file switched to system-managed - Windows will resize it (on the system volume) after a restart.");
            else
                ok = false;
        }
        catch
        {
            ok = false;
        }

        return (ok, notes);
    }

    // ---------------------------------------------------------------------------------------
    // Small shared helpers - same shape as ForcedCrashService/MinidumpHousekeepingService's own
    // registry read/write helpers, kept local since this service has its own two-key set
    // (CrashControl + Power) rather than sharing either of theirs.
    // ---------------------------------------------------------------------------------------

    private static string? ExpandEnvironment(string? value) =>
        string.IsNullOrEmpty(value) ? value : Environment.ExpandEnvironmentVariables(value);

    private static string? ExtractVolume(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 2 || path[1] != ':') return null;
        return path.Substring(0, 2);
    }

    private static int? ReadDwordAsInt(string path, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            var v = key?.GetValue(valueName);
            return v is null ? null : Convert.ToInt32(v);
        }
        catch { return null; }
    }

    private static bool? ReadDwordAsBool(string path, string valueName)
    {
        var v = ReadDwordAsInt(path, valueName);
        return v is null ? null : v != 0;
    }

    private static string? ReadString(string path, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    private static bool WriteDword(string path, string valueName, int value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
            if (key is null) return false;
            key.SetValue(valueName, value, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }

    private static bool WriteString(string path, string valueName, string value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
            if (key is null) return false;
            key.SetValue(valueName, value, RegistryValueKind.String);
            return true;
        }
        catch { return false; }
    }

    private static bool DeleteValue(string path, string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (key is null) return true; // nothing to remove
            if (key.GetValue(valueName) is not null) key.DeleteValue(valueName, throwOnMissingValue: false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Shells out and captures combined stdout+stderr under a bounded timeout - the same
    /// concurrent-read/kill-on-timeout pattern PowerPlanService.RunCapturedAsync/
    /// TracerouteService.RunAsync already establish elsewhere in this app.</summary>
    private static async Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 10000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");

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
            return ("(command timed out)", null);
        }

        string output = (await outputTask) + (await errorTask);
        return (output, proc.ExitCode);
    }
}
