using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #724-731: parses `bcdedit /enum all /v` and `bcdedit /enum firmware` into a structured
/// <see cref="BcdStore"/>, shared by every Boot configuration feature on the Startup tab so
/// bcdedit is shelled out to exactly twice per refresh (once for the BCD store, once for
/// firmware), not once per feature. bcdedit's text output is the documented, stable contract for
/// BCD data (see CLAUDE.md's "prefer a known Windows tool/API over raw interop" convention - this
/// app never touches the BCD registry hive's binary layout directly), so the parser reads it
/// adaptively as name/value pairs per block rather than hardcoding a fixed option set per entry
/// type (see BcdEntry's remarks). All mutating actions here are thin, single-purpose wrappers
/// around one bcdedit invocation each - every one of them is only ever called after the caller
/// has already shown the exact command in a confirmation dialog (see StartupViewModel's BCD
/// command handlers), matching CLAUDE.md's "mutating actions require explicit confirmation" rule.
/// </summary>
public static class BcdInspectorService
{
    // A line of 3+ dashes is bcdedit's own underline beneath each block's header text - the only
    // reliable block-boundary marker in the output (blank lines alone aren't, since some blocks
    // have none between them in certain locales/builds).
    private static readonly Regex DashLineRegex = new(@"^-{3,}$", RegexOptions.Compiled);

    // A real "name  value" line always starts at column 0 (no leading whitespace) with at least
    // two spaces separating the name from its value - bcdedit pads names to a fixed column width.
    // A header line ("Windows Boot Manager") never contains a run of 2+ spaces, so it naturally
    // fails this pattern and falls through to the header-candidate branch below.
    private static readonly Regex KeyValueRegex = new(@"^(\S.*?)\s{2,}(\S.*)$", RegexOptions.Compiled);

    // A continuation line for a multi-valued option (e.g. a second/third displayorder entry) is
    // indented to align under the value column, with no name of its own.
    private static readonly Regex ContinuationRegex = new(@"^\s+(\S.*)$", RegexOptions.Compiled);

    private static string BackupDirectory => Path.Combine(AppPaths.SettingsDirectory, "BcdBackups");

    #region #724: read + parse

    /// <summary>Reads and parses one full BCD + firmware snapshot. Never throws - a failed
    /// bcdedit invocation (not elevated enough, BCD store inaccessible) comes back as
    /// <c>Available: false</c> with <c>Error</c> set, so every dependent #724-731 feature can
    /// degrade to "unavailable" together rather than each re-deriving its own failure text.</summary>
    public static async Task<BcdStore> ReadAsync()
    {
        var (allOutput, allExit) = await RunCapturedAsync("bcdedit.exe", "/enum all /v");

        List<BcdEntry> entries;
        bool available;
        string? error;
        if (allExit == 0)
        {
            entries = ParseEntries(allOutput);
            available = entries.Count > 0;
            error = available ? null : "bcdedit ran, but no boot entries could be parsed from its output.";
        }
        else
        {
            entries = new List<BcdEntry>();
            available = false;
            error = string.IsNullOrWhiteSpace(allOutput) ? "bcdedit.exe failed to run (needs Administrator)." : allOutput.Trim();
        }

        var (fwOutput, fwExit) = await RunCapturedAsync("bcdedit.exe", "/enum firmware");
        var firmwareEntries = fwExit == 0 ? ParseEntries(fwOutput) : new List<BcdEntry>();

        return new BcdStore
        {
            Available = available,
            Error = error,
            Entries = entries,
            FirmwareEntries = firmwareEntries,
        };
    }

    /// <summary>Adaptive line-by-line parse of one `bcdedit /enum ...` text block set - see the
    /// class remarks and the regex fields above for the shape this relies on.</summary>
    private static List<BcdEntry> ParseEntries(string text)
    {
        var entries = new List<BcdEntry>();
        if (string.IsNullOrWhiteSpace(text)) return entries;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        BcdEntry? current = null;
        string? lastKey = null;
        string? pendingHeader = null;

        foreach (var rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            if (line.Length == 0) continue; // spacing only - real block boundaries are the dash-underline below

            if (DashLineRegex.IsMatch(line))
            {
                if (pendingHeader is not null)
                {
                    current = new BcdEntry { Header = pendingHeader };
                    entries.Add(current);
                    lastKey = null;
                    pendingHeader = null;
                }
                continue;
            }

            var kvMatch = KeyValueRegex.Match(line);
            if (kvMatch.Success)
            {
                string key = kvMatch.Groups[1].Value.Trim();
                string value = kvMatch.Groups[2].Value.Trim();

                if (current is null)
                {
                    // No block confirmed yet (its dash-underline hasn't been seen) - can't happen
                    // for well-formed bcdedit output, but degrade safely rather than throw.
                    pendingHeader = line;
                    continue;
                }

                if (!current.Options.TryGetValue(key, out var list))
                    current.Options[key] = list = new List<string>();
                list.Add(value);
                lastKey = key;

                if (key.Equals("identifier", StringComparison.OrdinalIgnoreCase))
                    current.Identifier = value;

                pendingHeader = null;
                continue;
            }

            var contMatch = ContinuationRegex.Match(line);
            if (contMatch.Success && current is not null && lastKey is not null && current.Options.TryGetValue(lastKey, out var values))
            {
                values.Add(contMatch.Groups[1].Value.Trim());
                continue;
            }

            // Neither a key/value line nor a continuation - the header text of the block that's
            // about to start, confirmed once the next line's dash-underline follows it.
            pendingHeader = line.Trim();
        }

        return entries;
    }

    #endregion

    #region #725: boot-mode/integrity flags

    /// <summary>#725: scans the current loader entry for the documented set of boot-mode/driver-
    /// signature-integrity flags. Each is worded as a quick flag, not a verdict (see BootModeFlag's
    /// remarks) - testsigning/debug/safeboot are all routinely on deliberately.</summary>
    public static List<BootModeFlag> DetectBootModeFlags(BcdEntry? current)
    {
        var flags = new List<BootModeFlag>();
        if (current is null) return flags;
        string id = current.Identifier;

        if (current.Get("safeboot") is { } safeboot && !string.IsNullOrWhiteSpace(safeboot))
        {
            flags.Add(new BootModeFlag
            {
                OptionName = "safeboot",
                RawValue = safeboot,
                Message = $"This boot entry is configured to always start in Safe Mode ({safeboot}) - most drivers/services are skipped every time it's used.",
                ClearCommandArgs = $"/deletevalue {id} safeboot",
            });
        }

        void AddIfYes(string option, string message)
        {
            var v = current.Get(option);
            if (v is not null && v.Equals("Yes", StringComparison.OrdinalIgnoreCase))
            {
                flags.Add(new BootModeFlag
                {
                    OptionName = option,
                    RawValue = v,
                    Message = message,
                    ClearCommandArgs = $"/deletevalue {id} {option}",
                });
            }
        }

        AddIfYes("testsigning", "Test-mode driver signing is on - unsigned/test-signed drivers can load, and Windows shows a \"Test Mode\" desktop watermark.");
        AddIfYes("nointegritychecks", "Driver signature integrity checks are off - any driver, signed or not, can load.");
        AddIfYes("disableintegritychecks", "Driver signature integrity checks are off - any driver, signed or not, can load.");
        AddIfYes("debug", "Kernel debugging is enabled for this boot entry.");
        AddIfYes("bootdebug", "Boot-manager-level debugging is enabled for this boot entry.");
        AddIfYes("flightsigning", "Flight (Windows Insider) signed drivers are allowed to load.");

        foreach (var loadOptions in current.GetAll("loadoptions"))
        {
            var tokens = loadOptions.Split(new[] { ' ', ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (!tokens.Any(t => t.Equals("DDISABLE_INTEGRITY_CHECKS", StringComparison.OrdinalIgnoreCase)))
                continue;

            string remainder = string.Join(" ", tokens.Where(t => !t.Equals("DDISABLE_INTEGRITY_CHECKS", StringComparison.OrdinalIgnoreCase)));
            string clearArgs = remainder.Length == 0
                ? $"/deletevalue {id} loadoptions"
                : $"/set {id} loadoptions \"{remainder}\"";

            flags.Add(new BootModeFlag
            {
                OptionName = "loadoptions",
                RawValue = loadOptions,
                Message = "loadoptions includes DDISABLE_INTEGRITY_CHECKS - driver signature enforcement is disabled via boot load options.",
                ClearCommandArgs = clearArgs,
            });
        }

        return flags;
    }

    /// <summary>Runs the exact bcdedit command a BootModeFlag's ClearCommandText already showed
    /// the user in the confirmation dialog - see StartupViewModel.ClearBootModeFlagAsync.</summary>
    public static async Task<(bool Success, string? Error)> ClearBootModeFlagAsync(BootModeFlag flag)
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("bcdedit.exe", flag.ClearCommandArgs);
            return exitCode == 0 ? (true, null) : (false, string.IsNullOrWhiteSpace(output) ? "bcdedit failed." : output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    #endregion

    #region #726 support: CPU/RAM totals for #727's comparison

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    /// <summary>#727: this system's real logical-processor count and installed RAM, so a
    /// performance-trap option's raw value can be compared against reality rather than shown as
    /// a bare number.</summary>
    public static (int LogicalProcessors, long TotalRamBytes) ReadSystemTotals()
    {
        long totalRam = 0;
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status)) totalRam = (long)status.ullTotalPhys;
        }
        catch
        {
            // Leave 0 - the comparison text below just omits the RAM figure when it's 0/unknown.
        }
        return (Environment.ProcessorCount, totalRam);
    }

    #endregion

    #region #727: performance-trap BCD options

    private static readonly Dictionary<string, string> PerfTrapDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["numproc"] = "Caps the number of logical processors Windows will use at boot.",
        ["maxproc"] = "Caps the number of logical processors Windows will use at boot.",
        ["truncatememory"] = "Caps the highest physical memory address Windows will use - installed RAM above this address is invisible to Windows.",
        ["removememory"] = "Excludes a fixed amount of physical memory from Windows' use.",
        ["increaseuserva"] = "Increases the per-process user-mode virtual address space (32-bit Windows only) at the cost of kernel address space.",
        ["usephysicaldestination"] = "Forces physical (rather than logical) APIC interrupt destination mode - a compatibility workaround that can affect interrupt routing performance.",
        ["useplatformclock"] = "Forces the platform's HPET clock source instead of Windows' preferred timer - can affect timer-sensitive workload performance.",
        ["disabledynamictick"] = "Disables the dynamic tick (timer-coalescing) power-saving feature - trades some power efficiency for more consistent timer latency.",
    };

    /// <summary>#727: detects numproc/maxproc/truncatememory/removememory/increaseuserva/
    /// usephysicaldestination/useplatformclock/disabledynamictick on the current loader entry,
    /// with the observed effect compared against this machine's real CPU/RAM totals where the
    /// option's value can be quantified.</summary>
    public static List<PerformanceTrapOption> DetectPerformanceTrapOptions(BcdEntry? current, int logicalProcessors, long totalRamBytes)
    {
        var results = new List<PerformanceTrapOption>();
        if (current is null) return results;

        foreach (var (name, baseEffect) in PerfTrapDescriptions)
        {
            var raw = current.Get(name);
            if (raw is null) continue;

            string effect = baseEffect;
            if ((name.Equals("numproc", StringComparison.OrdinalIgnoreCase) || name.Equals("maxproc", StringComparison.OrdinalIgnoreCase))
                && int.TryParse(raw, out int n) && logicalProcessors > 0)
            {
                effect += $" Observed: {n} of this system's {logicalProcessors} logical processors would be used.";
            }
            else if (name.Equals("truncatememory", StringComparison.OrdinalIgnoreCase) && TryParseSizeValue(raw, out long capBytes) && totalRamBytes > 0)
            {
                effect += $" Observed: caps usable RAM at about {Formatting.FormatBytes(capBytes)} of {Formatting.FormatBytes(totalRamBytes)} installed.";
            }
            else if (name.Equals("removememory", StringComparison.OrdinalIgnoreCase) && TryParseSizeValue(raw, out long removedBytes) && totalRamBytes > 0)
            {
                effect += $" Observed: about {Formatting.FormatBytes(removedBytes)} of {Formatting.FormatBytes(totalRamBytes)} installed RAM would be hidden from Windows.";
            }

            results.Add(new PerformanceTrapOption { OptionName = name, RawValue = raw, ObservedEffect = effect });
        }

        return results;
    }

    private static bool TryParseSizeValue(string raw, out long bytes)
    {
        raw = raw.Trim();
        try
        {
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                bytes = Convert.ToInt64(raw, 16);
                return true;
            }
            return long.TryParse(raw, out bytes);
        }
        catch
        {
            bytes = 0;
            return false;
        }
    }

    #endregion

    #region #728: boot status policy / auto-repair audit

    public static BootStatusPolicyInfo ReadBootStatusPolicy(BcdEntry? current) => new()
    {
        BootStatusPolicy = current?.Get("bootstatuspolicy"),
        RecoveryEnabled = current?.Get("recoveryenabled"),
    };

    /// <summary>#728: restores bootstatuspolicy/recoveryenabled to Windows defaults (deleting the
    /// override entirely, and explicitly re-enabling recovery) - only ever called after the caller
    /// has shown both commands in a confirmation dialog.</summary>
    public static async Task<(bool Success, string? Error)> RestoreBootStatusPolicyDefaultsAsync(string entryIdentifier)
    {
        try
        {
            var (out1, exit1) = await RunCapturedAsync("bcdedit.exe", $"/deletevalue {entryIdentifier} bootstatuspolicy");
            if (exit1 != 0) return (false, string.IsNullOrWhiteSpace(out1) ? "bcdedit /deletevalue bootstatuspolicy failed." : out1.Trim());

            var (out2, exit2) = await RunCapturedAsync("bcdedit.exe", $"/set {entryIdentifier} recoveryenabled yes");
            return exit2 == 0 ? (true, null) : (false, string.IsNullOrWhiteSpace(out2) ? "bcdedit /set recoveryenabled failed." : out2.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    #endregion

    #region #729: boot menu / multi-OS entry list

    /// <summary>#729: {bootmgr}'s timeout/displaybootmenu/default/displayorder, resolved to
    /// friendly names via BcdStore.DescribeIdentifier.</summary>
    public static BootMenuInfo ReadBootMenuInfo(BcdStore store)
    {
        var mgr = store.WindowsBootManager;
        int? timeout = mgr?.Get("timeout") is { } t && int.TryParse(t, out var ts) ? ts : null;
        bool? displayMenu = mgr?.Get("displaybootmenu") is { } dbm ? dbm.Equals("Yes", StringComparison.OrdinalIgnoreCase) : null;
        string? defaultId = mgr?.Get("default");

        var order = (mgr?.GetAll("displayorder") ?? Array.Empty<string>())
            .Select(id => new BootMenuEntryRef
            {
                Identifier = id,
                Description = store.DescribeIdentifier(id),
                IsDefault = defaultId is not null && id.Equals(defaultId, StringComparison.OrdinalIgnoreCase),
            })
            .ToList();

        return new BootMenuInfo
        {
            TimeoutSeconds = timeout,
            DisplayBootMenu = displayMenu,
            DefaultIdentifier = defaultId,
            DefaultDescription = defaultId is null ? "Unknown" : store.DescribeIdentifier(defaultId),
            DisplayOrder = order,
        };
    }

    public static async Task<(bool Success, string? Error)> SetTimeoutAsync(int seconds)
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("bcdedit.exe", $"/timeout {seconds}");
            return exitCode == 0 ? (true, null) : (false, string.IsNullOrWhiteSpace(output) ? "bcdedit /timeout failed." : output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static async Task<(bool Success, string? Error)> SetDefaultEntryAsync(string identifier)
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("bcdedit.exe", $"/default {identifier}");
            return exitCode == 0 ? (true, null) : (false, string.IsNullOrWhiteSpace(output) ? "bcdedit /default failed." : output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    #endregion

    #region #730: UEFI firmware boot order

    /// <summary>#730: {fwbootmgr}'s displayorder from `bcdedit /enum firmware`, flagged when
    /// Windows Boot Manager isn't first, with a ready-to-copy fix command - see
    /// FirmwareBootOrderInfo's remarks for why "stale" is a best-effort flag, not a verdict.</summary>
    public static FirmwareBootOrderInfo ReadFirmwareBootOrder(BcdStore store)
    {
        var fwMgr = store.FirmwareBootManager;
        var orderIds = fwMgr?.GetAll("displayorder") ?? Array.Empty<string>();
        var wbmEntry = store.FirmwareEntries.FirstOrDefault(e =>
            (e.Get("description") ?? string.Empty).Contains("Windows Boot Manager", StringComparison.OrdinalIgnoreCase));

        var order = orderIds.Select(id =>
        {
            var match = store.FirmwareEntries.FirstOrDefault(e => e.Identifier.Equals(id, StringComparison.OrdinalIgnoreCase));
            string desc = match?.Get("description") ?? string.Empty;
            return new FirmwareBootEntry
            {
                Identifier = id,
                Description = string.IsNullOrWhiteSpace(desc) ? "(no description - possibly a stale entry pointing at a removed device)" : desc,
                LooksStale = string.IsNullOrWhiteSpace(desc),
            };
        }).ToList();

        bool wbmFirst = order.Count > 0 && wbmEntry is not null
            && order[0].Identifier.Equals(wbmEntry.Identifier, StringComparison.OrdinalIgnoreCase);

        string? fixCmd = null;
        if (!wbmFirst && wbmEntry is not null && order.Count > 0)
        {
            var reordered = new List<string> { wbmEntry.Identifier };
            reordered.AddRange(order.Select(o => o.Identifier).Where(id => !id.Equals(wbmEntry.Identifier, StringComparison.OrdinalIgnoreCase)));
            fixCmd = $"bcdedit /set {{fwbootmgr}} displayorder {string.Join(" ", reordered)}";
        }

        return new FirmwareBootOrderInfo
        {
            DisplayOrder = order,
            WindowsBootManagerFirst = wbmFirst,
            SuggestedFixCommand = fixCmd,
        };
    }

    #endregion

    #region #731: BCD backup and export

    /// <summary>#731: every backup this app has exported, newest first - a plain directory
    /// listing under AppPaths.SettingsDirectory\BcdBackups rather than a separate JSON index,
    /// since the backup files themselves are the source of truth here.</summary>
    public static List<BcdBackupEntry> ListBackups()
    {
        var result = new List<BcdBackupEntry>();
        try
        {
            if (!Directory.Exists(BackupDirectory)) return result;
            foreach (var file in Directory.GetFiles(BackupDirectory, "bcd-backup-*"))
            {
                try
                {
                    result.Add(new BcdBackupEntry { FilePath = file, CreatedUtc = File.GetCreationTimeUtc(file) });
                }
                catch
                {
                    // One unreadable entry (deleted mid-listing, permissions) - skip it.
                }
            }
        }
        catch
        {
            // Directory listing failed entirely - empty list, same degrade-to-nothing pattern as
            // every other on-demand read in this app.
        }
        return result.OrderByDescending(b => b.CreatedUtc).ToList();
    }

    /// <summary>#731: `bcdedit /export` to a fresh timestamped file under BackupDirectory. Always
    /// called before any BCD-modifying action this app offers (see StartupViewModel's
    /// BackupThenRunAsync) - restore is never automated by this app; only the matching
    /// `bcdedit /import` command is ever shown, for the user to run themselves.</summary>
    public static async Task<(bool Success, string? Path, string? Error)> ExportBackupAsync()
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            string path = Path.Combine(BackupDirectory, $"bcd-backup-{DateTime.Now:yyyyMMdd-HHmmss}");
            var (output, exitCode) = await RunCapturedAsync("bcdedit.exe", $"/export \"{path}\"");
            return exitCode == 0 ? (true, path, null) : (false, null, string.IsNullOrWhiteSpace(output) ? "bcdedit /export failed." : output.Trim());
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    #endregion

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's historical default timeout.</summary>
    private static Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 10000)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs);
}
