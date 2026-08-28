using System.Diagnostics;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #184-190: "kernel, storage and driver event families" - each public method here queries one
/// specific, named event-ID family (storage-fault, shadow-copy, WHEA, driver-load-failure, chkdsk,
/// memory-diagnostic, power-transition) and returns an already-summarized/grouped result, feeding
/// one Stability-tab card apiece. Every log read reuses EventLogExplorerService.ReadPage (via the
/// private ReadFamily helper below) rather than adding a fifth event-log reading path alongside
/// EventLogService/EventLogExplorerService/EventAnomalyDetectionService/EventTimelineService.
/// Every method here follows the same "degrade to empty/Unknown/hidden, never fabricate, never
/// throw out of the service" rule as the rest of this app's Services/ layer - a locked-down
/// channel, an event whose message text doesn't match the expected shape, or a shelled-out tool
/// that isn't present are all real, expected conditions, not bugs.
/// </summary>
public sealed class KernelEventFamilyService
{
    private const int LookbackDays = 30;

    // Chkdsk/autochk and Windows Memory Diagnostic runs are rare, boot-time-only events - a 30-day
    // window would show "nothing found" on most PCs even when a run happened a few months ago, so
    // these two use their own, much wider windows instead of LookbackDays.
    private const int ChkdskLookbackDays = 180;
    private const int MemoryDiagnosticLookbackDays = 3650; // ~effectively "has this ever run"

    private readonly EventLogExplorerService _explorer;

    public KernelEventFamilyService() : this(new EventLogExplorerService()) { }

    public KernelEventFamilyService(EventLogExplorerService explorer) => _explorer = explorer;

    // ==================== #184: storage error family, grouped by physical disk ====================

    private static readonly Regex HarddiskPathRegex = new(@"\\Device\\Harddisk(\d+)\\DR\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>#184: queries the whole storage-fault family (disk 7/11/51/153, Microsoft-Windows-
    /// Ntfs 55/98/137/140, storahci/stornvme/iaStorA 129, volmgr 46) and groups by the
    /// \Device\HarddiskN\DRn path parsed out of each event's own message text, mapped to a friendly
    /// model/letter via SystemSpecsService.ListDisksForSmart and this service's own disk-index ->
    /// drive-letter WMI join - "which physical disk is throwing errors" instead of a flat count.</summary>
    public List<StorageErrorDiskGroup> ReadStorageErrors(int lookbackDays = LookbackDays)
    {
        var pairs = new (string Provider, int EventId)[]
        {
            ("disk", 7), ("disk", 11), ("disk", 51), ("disk", 153),
            ("Microsoft-Windows-Ntfs", 55), ("Microsoft-Windows-Ntfs", 98), ("Microsoft-Windows-Ntfs", 137), ("Microsoft-Windows-Ntfs", 140),
            ("storahci", 129), ("stornvme", 129), ("iaStorA", 129),
            ("volmgr", 46),
        };
        var rows = ReadFamily("System", pairs, lookbackDays);
        if (rows.Count == 0) return new List<StorageErrorDiskGroup>();

        var models = SafeListDiskModels();
        var letters = ReadDriveLettersByDiskIndex();

        return rows
            .GroupBy(r => HarddiskPathRegex.Match(r.Message) is { Success: true } m ? int.Parse(m.Groups[1].Value) : -1)
            .Select(g =>
            {
                string friendly;
                if (g.Key < 0)
                {
                    friendly = "Unknown disk";
                }
                else
                {
                    string model = models.TryGetValue(g.Key, out var mm) ? mm : $"Disk {g.Key}";
                    string letterPart = letters.TryGetValue(g.Key, out var ls) && ls.Count > 0
                        ? $" ({string.Join(", ", ls)})"
                        : string.Empty;
                    friendly = $"{model}{letterPart}";
                }

                var hits = g.GroupBy(r => (r.ProviderName, r.EventId))
                    .Select(hg => new StorageErrorHit
                    {
                        Provider = hg.Key.ProviderName,
                        EventId = hg.Key.EventId,
                        Count = hg.Count(),
                        LastSeen = hg.Max(r => r.TimeCreated),
                        Description = StorageErrorDescription(hg.Key.ProviderName, hg.Key.EventId),
                    })
                    .OrderByDescending(h => h.Count)
                    .ToList();

                return new StorageErrorDiskGroup
                {
                    DiskIndex = g.Key,
                    FriendlyName = friendly,
                    TotalCount = g.Count(),
                    LastSeen = g.Max(r => r.TimeCreated),
                    Hits = hits,
                };
            })
            .OrderByDescending(x => x.TotalCount)
            .ToList();
    }

    private static string StorageErrorDescription(string provider, int eventId) => (provider, eventId) switch
    {
        ("disk", 7) => "Device not ready yet",
        ("disk", 11) => "Controller error",
        ("disk", 51) => "Error during a paging operation",
        ("disk", 153) => "Controller error on a retried I/O operation",
        ("Microsoft-Windows-Ntfs", 55) => "File system structure corrupt",
        ("Microsoft-Windows-Ntfs", 98) => "Volume dirty bit set",
        ("Microsoft-Windows-Ntfs", 137) => "Volume change journal deleted",
        ("Microsoft-Windows-Ntfs", 140) => "Transaction resource manager error",
        ("storahci", 129) or ("stornvme", 129) or ("iaStorA", 129) => "Device reset - did not respond in time",
        ("volmgr", 46) => "Volume manager error",
        _ => "Storage error",
    };

    private static Dictionary<int, string> SafeListDiskModels()
    {
        try { return SystemSpecsService.ListDisksForSmart().ToDictionary(d => d.Index, d => d.Model); }
        catch { return new Dictionary<int, string>(); }
    }

    private static readonly Regex QuotedDeviceIdRegex = new(@"\.DeviceID\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled);
    private static readonly Regex PhysicalDriveIndexRegex = new(@"PHYSICALDRIVE(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DriveLetterRegex = new(@"^([A-Za-z]):$", RegexOptions.Compiled);

    /// <summary>#184: no existing service in this codebase maps a physical disk index to its
    /// current drive letter(s) (SystemSpecsService.ListDisksForSmart only carries index+model), so
    /// this joins Win32_DiskDriveToDiskPartition (disk -> partition) with
    /// Win32_LogicalDiskToPartition (partition -> drive letter) the same way Windows' own Disk
    /// Management associates them - both classes report their endpoints as full WMI object paths
    /// ("...Win32_DiskDrive.DeviceID=\"\\\\.\\PHYSICALDRIVE0\"" / "...Win32_DiskPartition.DeviceID=
    /// \"Disk #0, Partition #0\""), so QuotedDeviceIdRegex pulls out just the identifying value from
    /// each side rather than parsing the whole path string. Degrades to an empty map (friendly names
    /// fall back to model-only, no drive letters) on any WMI failure - a disk with no assigned drive
    /// letter (unallocated, or a raw disk) is expected, not an error.</summary>
    private static Dictionary<int, List<string>> ReadDriveLettersByDiskIndex()
    {
        var result = new Dictionary<int, List<string>>();
        try
        {
            var partitionToDisk = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var searcher = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition"))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    string antecedent = mo["Antecedent"]?.ToString() ?? string.Empty;
                    string dependent = mo["Dependent"]?.ToString() ?? string.Empty;

                    var diskMatch = PhysicalDriveIndexRegex.Match(antecedent);
                    var partMatch = QuotedDeviceIdRegex.Match(dependent);
                    if (diskMatch.Success && partMatch.Success && int.TryParse(diskMatch.Groups[1].Value, out int diskIdx))
                        partitionToDisk[partMatch.Groups[1].Value] = diskIdx;
                }
            }

            using (var searcher = new ManagementObjectSearcher("SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition"))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    string antecedent = mo["Antecedent"]?.ToString() ?? string.Empty;
                    string dependent = mo["Dependent"]?.ToString() ?? string.Empty;

                    var partMatch = QuotedDeviceIdRegex.Match(antecedent);
                    var letterMatch = QuotedDeviceIdRegex.Match(dependent);
                    if (!partMatch.Success || !letterMatch.Success) continue;
                    if (!partitionToDisk.TryGetValue(partMatch.Groups[1].Value, out int diskIdx)) continue;
                    if (!DriveLetterRegex.IsMatch(letterMatch.Groups[1].Value)) continue;

                    if (!result.TryGetValue(diskIdx, out var list)) result[diskIdx] = list = new List<string>();
                    list.Add(letterMatch.Groups[1].Value);
                }
            }
        }
        catch
        {
            // WMI association classes unavailable/denied - friendly names fall back to model-only.
        }
        return result;
    }

    // ==================== #185: shadow copy / VSS family ====================

    /// <summary>#185: volsnap (System log) 25/33/36 and classic "VSS" source (Application log)
    /// 8193/12289 events, plus vssadmin's own current shadow-storage allocation snapshot - silently
    /// deleted shadow copies mean System Restore and File History are quietly not working, so both
    /// are surfaced together rather than left buried in the general event list.</summary>
    public ShadowCopyStatus ReadShadowCopyStatus(int lookbackDays = LookbackDays)
    {
        var volsnapRows = ReadFamily("System", new[] { ("volsnap", 25), ("volsnap", 33), ("volsnap", 36) }, lookbackDays);
        var vssRows = ReadFamily("Application", new[] { ("VSS", 8193), ("VSS", 12289) }, lookbackDays);

        var events = volsnapRows.Concat(vssRows)
            .OrderByDescending(r => r.TimeCreated)
            .Select(r => new ShadowCopyEventInfo
            {
                TimeCreated = r.TimeCreated,
                Provider = r.ProviderName,
                EventId = r.EventId,
                Description = Truncate(r.Message, 300),
            })
            .ToList();

        var (volumes, error) = ReadShadowStorage();
        return new ShadowCopyStatus { Events = events, StorageVolumes = volumes, VssAdminError = error };
    }

    private static readonly Regex ShadowForVolumeRegex = new(@"For volume:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex ShadowStorageVolumeRegex = new(@"Shadow Copy Storage volume:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex ShadowUsedRegex = new(@"Used Shadow Copy Storage space:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex ShadowAllocatedRegex = new(@"Allocated Shadow Copy Storage space:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex ShadowMaxRegex = new(@"Maximum Shadow Copy Storage space:\s*(.+)", RegexOptions.Compiled);

    /// <summary>#185: shells out to `vssadmin list shadowstorage` and parses its text output - no
    /// WMI class or PowerShell-cmdlet-free structured API exposes this, so text parsing (this app's
    /// established fallback for vssadmin.exe elsewhere) is the only option.</summary>
    private static (List<ShadowStorageVolumeInfo> Volumes, string? Error) ReadShadowStorage()
    {
        try
        {
            var (output, exitCode) = RunCaptured("vssadmin.exe", "list shadowstorage", timeoutMs: 15000);
            var volumes = ParseShadowStorageOutput(output);
            if (volumes.Count == 0 && exitCode != 0)
                return (volumes, "vssadmin.exe reported no shadow-copy storage associations (no shadow copies configured, or access denied).");
            return (volumes, null);
        }
        catch (Exception ex)
        {
            return (new List<ShadowStorageVolumeInfo>(), $"Couldn't run vssadmin.exe: {ex.Message}");
        }
    }

    /// <summary>vssadmin separates each "Shadow Copy Storage association" block with a blank line -
    /// a block with no "For volume:" line is banner/copyright text, not a real association, and is
    /// skipped rather than added as an empty row.</summary>
    private static List<ShadowStorageVolumeInfo> ParseShadowStorageOutput(string output)
    {
        var results = new List<ShadowStorageVolumeInfo>();
        var blocks = Regex.Split(output.Replace("\r\n", "\n"), @"\n\s*\n");
        foreach (var block in blocks)
        {
            var forMatch = ShadowForVolumeRegex.Match(block);
            if (!forMatch.Success) continue;

            results.Add(new ShadowStorageVolumeInfo
            {
                ForVolume = forMatch.Groups[1].Value.Trim(),
                StorageVolume = ShadowStorageVolumeRegex.Match(block) is { Success: true } sv ? sv.Groups[1].Value.Trim() : string.Empty,
                UsedSpace = ShadowUsedRegex.Match(block) is { Success: true } us ? us.Groups[1].Value.Trim() : string.Empty,
                AllocatedSpace = ShadowAllocatedRegex.Match(block) is { Success: true } al ? al.Groups[1].Value.Trim() : string.Empty,
                MaximumSpace = ShadowMaxRegex.Match(block) is { Success: true } mx ? mx.Groups[1].Value.Trim() : string.Empty,
            });
        }
        return results;
    }

    // ==================== #186: WHEA corrected-hardware-error rate ====================

    /// <summary>#186: Microsoft-Windows-WHEA-Logger 17/18/19/47 per day over the lookback window -
    /// the same Date+Count shape EventLogService.BuildDailyCounts already uses for Reliability
    /// History, so the view can reuse the identical small-column-chart binding pattern. "Quick flag,
    /// not a verdict" - corrected errors (17/47) are not fatal by themselves.</summary>
    public WheaErrorSummary ReadWheaErrors(int lookbackDays = LookbackDays)
    {
        var pairs = new (string, int)[]
        {
            ("Microsoft-Windows-WHEA-Logger", 17), ("Microsoft-Windows-WHEA-Logger", 18),
            ("Microsoft-Windows-WHEA-Logger", 19), ("Microsoft-Windows-WHEA-Logger", 47),
        };
        var rows = ReadFamily("System", pairs, lookbackDays);

        var counts = rows.GroupBy(r => r.TimeCreated.Date).ToDictionary(g => g.Key, g => g.Count());
        var daily = new List<DailyEventCount>();
        var today = DateTime.Now.Date;
        for (int i = lookbackDays - 1; i >= 0; i--)
        {
            var day = today.AddDays(-i);
            daily.Add(new DailyEventCount { Date = day, Count = counts.TryGetValue(day, out var c) ? c : 0 });
        }

        return new WheaErrorSummary
        {
            DailyCounts = daily,
            TotalCount = rows.Count,
            LastSeen = rows.Count > 0 ? rows.Max(r => r.TimeCreated) : null,
        };
    }

    // ==================== #187: driver load failures ====================

    /// <summary>#187: Kernel-PnP 219/411/442 (tried against both the System log and the dedicated
    /// Microsoft-Windows-Kernel-PnP/Configuration channel some editions use instead - additive, a
    /// channel that doesn't exist/isn't enabled just contributes nothing) and UserPnp 20001/20003
    /// install results (Application log), each joined against `driverquery /v /fo csv` by module
    /// name so a failure names a real, currently-installed device/driver when one matches.</summary>
    public List<DriverFailureEvent> ReadDriverFailures(int lookbackDays = LookbackDays)
    {
        var pnpPairs = new (string, int)[]
        {
            ("Microsoft-Windows-Kernel-PnP", 219), ("Microsoft-Windows-Kernel-PnP", 411), ("Microsoft-Windows-Kernel-PnP", 442),
        };
        var userPnpPairs = new (string, int)[] { ("Microsoft-Windows-UserPnp", 20001), ("Microsoft-Windows-UserPnp", 20003) };

        var rows = ReadFamily("System", pnpPairs, lookbackDays);
        rows.AddRange(ReadFamily("Microsoft-Windows-Kernel-PnP/Configuration", pnpPairs, lookbackDays));
        rows.AddRange(ReadFamily("Application", userPnpPairs, lookbackDays));
        if (rows.Count == 0) return new List<DriverFailureEvent>();

        var drivers = ReadInstalledDrivers();
        var byModule = new Dictionary<string, InstalledDriverInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in drivers)
        {
            string key = NormalizeDriverName(d.ModuleName);
            if (key.Length > 0 && !byModule.ContainsKey(key)) byModule[key] = d;
        }

        var result = new List<DriverFailureEvent>();
        foreach (var r in rows.OrderByDescending(r => r.TimeCreated))
        {
            string? driverName = ExtractDriverNameFromProperties(r);
            InstalledDriverInfo? info = driverName is { Length: > 0 } && byModule.TryGetValue(NormalizeDriverName(driverName), out var found)
                ? found
                : null;

            result.Add(new DriverFailureEvent
            {
                TimeCreated = r.TimeCreated,
                Provider = r.ProviderName,
                EventId = r.EventId,
                DriverName = driverName,
                Description = Truncate(r.Message, 300),
                DriverInfo = info,
            });
        }
        return result;
    }

    /// <summary>Best-effort driver/device name from the event's own inserted properties - a
    /// property containing ".sys" is almost always the driver file name itself; failing that, the
    /// first non-empty property is usually the device/driver name too (the same positional,
    /// best-effort convention EventLogService.ExtractBugcheckCode uses for event 41's property
    /// 0).</summary>
    private static string? ExtractDriverNameFromProperties(EventRecordRow r)
    {
        var sysProp = r.PropertyValues.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && p.Contains(".sys", StringComparison.OrdinalIgnoreCase));
        if (sysProp is not null) return sysProp.Trim();
        return r.PropertyValues.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))?.Trim();
    }

    private static string NormalizeDriverName(string name)
    {
        name = name.Trim();
        int slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    /// <summary>#187: `driverquery /v /fo csv` - header parsed by name (not a hardcoded column
    /// index) so a slightly different column set across Windows versions still resolves the fields
    /// this app actually uses; a missing "Module Name" column (an unrecognized output shape) means
    /// nothing usable to join against, so this degrades to an empty list rather than guessing
    /// positions.</summary>
    private static List<InstalledDriverInfo> ReadInstalledDrivers()
    {
        var result = new List<InstalledDriverInfo>();
        try
        {
            var (output, _) = RunCaptured("driverquery.exe", "/v /fo csv", timeoutMs: 15000);
            var lines = output.Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToList();
            if (lines.Count < 2) return result;

            var header = ParseCsvLine(lines[0]).Select(h => h.Trim()).ToList();
            int IndexOf(string name) => header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
            int moduleIdx = IndexOf("Module Name");
            int displayIdx = IndexOf("Display Name");
            int startModeIdx = IndexOf("Start Mode");
            int stateIdx = IndexOf("State");
            int linkDateIdx = IndexOf("Link Date");
            int pathIdx = IndexOf("Path");
            if (moduleIdx < 0) return result; // unrecognized output shape - nothing usable to join against

            for (int i = 1; i < lines.Count; i++)
            {
                var fields = ParseCsvLine(lines[i]);
                string Field(int idx) => idx >= 0 && idx < fields.Count ? fields[idx].Trim() : string.Empty;
                string module = Field(moduleIdx);
                if (module.Length == 0) continue;

                result.Add(new InstalledDriverInfo
                {
                    ModuleName = module,
                    DisplayName = Field(displayIdx),
                    StartMode = Field(startModeIdx),
                    State = Field(stateIdx),
                    LinkDate = Field(linkDateIdx),
                    Path = Field(pathIdx),
                });
            }
        }
        catch
        {
            // driverquery.exe missing/blocked/timed out - degrade to no driver join; the raw event
            // rows on their own are still useful without it.
        }
        return result;
    }

    // ==================== #188: chkdsk / autochk results ====================

    private static readonly Regex ChkdskBadSectorsRegex = new(@"(\d+)\s*KB in bad sectors", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChkdskVolumeRegex = new(@"[Vv]olume label is\s*""?([^""\r\n.]+)""?", RegexOptions.Compiled);
    private static readonly string[] ChkdskCleanPhrases =
    {
        "found no problems", "no further action is required", "Windows has checked the file system and found no problems",
    };
    private static readonly string[] ChkdskFixedPhrases = { "made corrections to the file system" };

    /// <summary>#188: parses Wininit event 1001 (System log - carries the full boot-time autochk/
    /// chkdsk report as plain text) and Chkdsk 26212/26214 (Application log, the newer proactive-
    /// scan events) for bad sectors found and whether the run completed cleanly. Uses a much wider
    /// lookback than most other cards on this tab since these only run at boot when scheduled, so a
    /// 30-day window would read "nothing found" on most healthy PCs even with a recent run.</summary>
    public List<ChkdskRunInfo> ReadChkdskResults(int lookbackDays = ChkdskLookbackDays)
    {
        var results = new List<ChkdskRunInfo>();

        foreach (var r in ReadFamily("System", new[] { ("Microsoft-Windows-Wininit", 1001) }, lookbackDays))
            results.Add(BuildChkdskRun(r, "Wininit 1001"));

        foreach (var r in ReadFamily("Application", new[] { ("Chkdsk", 26212), ("Chkdsk", 26214) }, lookbackDays))
            results.Add(BuildChkdskRun(r, $"Chkdsk {r.EventId}"));

        return results.OrderByDescending(x => x.TimeCreated).ToList();
    }

    private static ChkdskRunInfo BuildChkdskRun(EventRecordRow r, string source)
    {
        var badMatch = ChkdskBadSectorsRegex.Match(r.Message);
        long? badKb = badMatch.Success && long.TryParse(badMatch.Groups[1].Value, out var kb) ? kb : null;
        var volMatch = ChkdskVolumeRegex.Match(r.Message);

        bool completedCleanly = badKb == 0
            || ChkdskCleanPhrases.Any(p => r.Message.Contains(p, StringComparison.OrdinalIgnoreCase))
            || ChkdskFixedPhrases.Any(p => r.Message.Contains(p, StringComparison.OrdinalIgnoreCase));

        return new ChkdskRunInfo
        {
            TimeCreated = r.TimeCreated,
            Source = source,
            Volume = volMatch.Success ? volMatch.Groups[1].Value.Trim() : null,
            BadSectorsFoundKb = badKb,
            CompletedCleanly = completedCleanly,
            RawSummary = Truncate(r.Message, 800),
        };
    }

    // ==================== #189: Windows Memory Diagnostic results ====================

    /// <summary>#189: Microsoft-Windows-MemoryDiagnostics-Results 1101/1201 (System log) - the
    /// outcome and date of the last memory test, or "never run" when nothing was found within the
    /// (deliberately very long) lookback this query uses. Pairs with the WHEA card above - "was this
    /// RAM ever actually tested".</summary>
    public MemoryDiagnosticStatus ReadMemoryDiagnosticStatus(int lookbackDays = MemoryDiagnosticLookbackDays)
    {
        var rows = ReadFamily("System",
            new[] { ("Microsoft-Windows-MemoryDiagnostics-Results", 1101), ("Microsoft-Windows-MemoryDiagnostics-Results", 1201) },
            lookbackDays);
        if (rows.Count == 0) return new MemoryDiagnosticStatus { HasEverRun = false };

        var last = rows.OrderByDescending(r => r.TimeCreated).First();
        return new MemoryDiagnosticStatus
        {
            HasEverRun = true,
            LastRunTime = last.TimeCreated,
            Outcome = Truncate(last.Message, 300),
        };
    }

    // ==================== #190: power-transition failure family ====================

    /// <summary>#190: Kernel-Power 125/126/131 (device power-transition failures) and
    /// Kernel-Processor-Power 37/55 (processor power/throttling) - a historical incident record,
    /// deliberately separate from (not merged with) the existing live thermal-throttle/power-limit
    /// flag elsewhere in this app.</summary>
    public List<PowerTransitionIncident> ReadPowerTransitionIncidents(int lookbackDays = LookbackDays)
    {
        var pairs = new (string, int)[]
        {
            ("Microsoft-Windows-Kernel-Power", 125), ("Microsoft-Windows-Kernel-Power", 126), ("Microsoft-Windows-Kernel-Power", 131),
            ("Microsoft-Windows-Kernel-Processor-Power", 37), ("Microsoft-Windows-Kernel-Processor-Power", 55),
        };
        var rows = ReadFamily("System", pairs, lookbackDays);

        return rows.OrderByDescending(r => r.TimeCreated)
            .Select(r => new PowerTransitionIncident
            {
                TimeCreated = r.TimeCreated,
                Provider = r.ProviderName,
                EventId = r.EventId,
                DeviceName = ExtractDriverNameFromProperties(r),
                Description = Truncate(r.Message, 300),
            })
            .ToList();
    }

    // ==================== shared helpers ====================

    /// <summary>Builds the same style of provider+eventId-scoped XPath EventLogService.
    /// ScanForKnownBadIds already uses, then reads it via EventLogExplorerService.ReadPage (a single
    /// unbookmarked page, capped generously) rather than a second event-log reading path. A channel
    /// that doesn't exist or isn't enabled (e.g. an Analytic/Debug channel, or a driver-specific
    /// provider absent on this PC) comes back as ReadPage's ErrorText, which this degrades to an
    /// empty list for - exactly the same "one family member unavailable doesn't blank out the
    /// others" tolerance every multi-part read on this tab already has.</summary>
    private List<EventRecordRow> ReadFamily(string logName, IEnumerable<(string Provider, int EventId)> pairs, int lookbackDays, int pageSize = 1000)
    {
        var list = pairs.ToList();
        if (list.Count == 0) return new List<EventRecordRow>();

        string idsClause = string.Join(" or ", list
            .GroupBy(f => f.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"(Provider[@Name={EventLogExplorerService.QuoteXPathLiteral(g.Key)}] and ({string.Join(" or ", g.Select(f => $"EventID={f.EventId}"))}))"));

        long maxAgeMs = lookbackDays * 24L * 60 * 60 * 1000;
        string xpath = $"*[System[({idsClause}) and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]";

        try
        {
            var result = _explorer.ReadPage(logName, xpath, null, pageSize);
            return result.ErrorText is null ? result.Rows : new List<EventRecordRow>();
        }
        catch
        {
            return new List<EventRecordRow>();
        }
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";

    /// <summary>Shells out and captures combined stdout+stderr, bounded by a real timeout - both
    /// reads are started before the bounded wait (so a child that fills a pipe buffer before exiting
    /// can't deadlock the caller), the same pattern EtwTraceService.RunCapturedAsync established;
    /// this is the synchronous variant since every caller here already runs off the UI thread via
    /// Task.Run from the ViewModel.</summary>
    private static (string Output, int ExitCode) RunCaptured(string exe, string args, int timeoutMs)
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

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return ("(command timed out)", -1);
        }

        string output = outputTask.GetAwaiter().GetResult() + errorTask.GetAwaiter().GetResult();
        return (output, proc.ExitCode);
    }

    /// <summary>Minimal CSV line parser (quoted fields, "" as an escaped quote) - .NET has no
    /// built-in CSV reader and pulling in a package for one `driverquery /v /fo csv` parse isn't
    /// worth a new dependency.</summary>
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
