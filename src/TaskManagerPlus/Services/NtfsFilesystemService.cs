using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, #337/#338/#339/#343/#345: NTFS/volume-metadata facts and actions that come from a
/// single MSFT_Volume query (root\Microsoft\Windows\Storage) plus a handful of fsutil/chkntfs
/// shell-outs and one registry read - all cheap enough to read once at Storage-tab load time (or on
/// an explicit refresh click), never on a poll tick, per this round's brief. Every fsutil/chkntfs
/// call goes through RunToolAsync at the bottom of this file, which centralizes the app's standard
/// shell-out shape (concurrent stdout/stderr reads + a bounded wait + Kill()-on-timeout, the same
/// pattern VolumeDiagnosticsService/DiskFragmentationService/BadSectorService/ClusterMappingService
/// each inline once) - centralized here specifically because this one file needs that exact shape
/// five separate times, unlike any of those single-shell-out-per-file services.
/// </summary>
public static class NtfsFilesystemService
{
    private const string SessionManagerKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager";

    // ================================================================================
    // #345: per-volume filesystem facts card - the anchor the rest of this file's actions attach to.
    // ================================================================================

    /// <summary>One MSFT_Volume query, decoded per Microsoft's documented enums for this class
    /// (confirmed against a live query on a real machine while building this - FileSystemType 14 ==
    /// "NTFS", DedupMode 4 == "Not available", both matched the FileSystem/DedupMode text below).
    /// Skips volumes with no assigned drive letter (unlettered system/recovery partitions) - this
    /// card is about the fixed, user-visible volumes fsutil/chkdsk/chkntfs act on by letter anyway.
    /// </summary>
    public static List<VolumeFilesystemFacts> ListVolumes()
    {
        var result = new List<VolumeFilesystemFacts>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DriveLetter, FileSystem, FileSystemLabel, HealthStatus, OperationalStatus, AllocationUnitSize, DedupMode FROM MSFT_Volume");
            foreach (ManagementObject vol in searcher.Get())
            {
                try
                {
                    string? driveLetter = ReadDriveLetter(vol["DriveLetter"]);
                    if (driveLetter is null) continue;

                    string fileSystem = (vol["FileSystem"] as string ?? string.Empty).Trim();
                    if (fileSystem.Length == 0) fileSystem = "Unknown";
                    bool isNtfs = string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);
                    bool isRefs = string.Equals(fileSystem, "ReFS", StringComparison.OrdinalIgnoreCase);

                    int health = 0;
                    try { health = Convert.ToInt32(vol["HealthStatus"] ?? 0); } catch { /* leave 0 (Healthy) */ }

                    uint? allocationUnit = null;
                    try { if (vol["AllocationUnitSize"] is not null) allocationUnit = Convert.ToUInt32(vol["AllocationUnitSize"]); }
                    catch { /* leave null */ }

                    int dedup = -1;
                    try { dedup = Convert.ToInt32(vol["DedupMode"] ?? -1); } catch { /* leave -1 (Unknown) */ }

                    result.Add(new VolumeFilesystemFacts
                    {
                        DriveLetter = driveLetter,
                        FileSystemLabel = (vol["FileSystemLabel"] as string ?? string.Empty).Trim(),
                        FileSystemName = fileSystem,
                        IsNtfs = isNtfs,
                        IsRefs = isRefs,
                        HealthStatus = VolumeHealthStatusName(health),
                        OperationalStatus = OperationalStatusArrayText(vol["OperationalStatus"]),
                        AllocationUnitSizeBytes = allocationUnit,
                        DedupModeText = DedupModeName(dedup),
                        PhysicalSectorSizeBytes = DiskFragmentationService.GetPhysicalSectorSizeBytes(driveLetter),
                        RefsIntegrityStreamsText = isRefs
                            ? "N/A (per-file integrity-stream state not queried here)"
                            : "N/A (not a ReFS volume)",
                    });
                }
                catch { /* skip this one volume, keep the rest */ }
            }
        }
        catch { /* namespace/class unavailable - empty list, same tier as StorageSpacesService.List */ }
        return result.OrderBy(v => v.DriveLetter, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>MSFT_Volume.DriveLetter is documented as Char16, but System.Management has been
    /// observed to marshal WMI CIM char properties as either a boxed System.Char or a one-character
    /// System.String depending on provider/OS build - handled defensively rather than assuming one.
    /// Null (not "Unknown") for a volume with no assigned letter, since that's a normal, common case
    /// (EFI System Partition, Recovery partition, ...) this card simply doesn't cover.</summary>
    private static string? ReadDriveLetter(object? raw) => raw switch
    {
        char c when c != '\0' => c.ToString(),
        string s when s.Length > 0 && s[0] != '\0' => s[..1],
        _ => null,
    };

    // MSFT_Volume.HealthStatus - a different enum from MSFT_VirtualDisk.HealthStatus
    // (StorageSpacesService's), which happens to share the same 0="Healthy" but diverges after that.
    private static string VolumeHealthStatusName(int code) => code switch
    {
        0 => "Healthy",
        1 => "Scan Needed",
        2 => "Spot Fix Needed",
        3 => "Full Repair Needed",
        _ => "Unknown",
    };

    // MSFT_Volume.DedupMode, documented enum.
    private static string DedupModeName(int code) => code switch
    {
        0 => "Disabled",
        1 => "General purpose",
        2 => "Hyper-V",
        3 => "Backup",
        4 => "Not available",
        _ => "Unknown",
    };

    /// <summary>Same common CIM OperationalStatus codes StorageSpacesService.OperationalStatusName
    /// decodes for MSFT_VirtualDisk - duplicated here rather than shared across classes (the same
    /// "small, self-contained duplication over a speculative shared helper" call this app already
    /// makes for StorageViewModel.TrendLineOf vs. PerformanceViewModel.LineOf).</summary>
    private static string OperationalStatusArrayText(object? raw)
    {
        if (raw is not ushort[] codes || codes.Length == 0) return string.Empty;
        return string.Join(", ", codes.Select(OperationalStatusName));
    }

    private static string OperationalStatusName(ushort code) => code switch
    {
        1 => "Other",
        2 => "OK",
        3 => "Degraded",
        4 => "Stressed",
        5 => "Predictive failure",
        6 => "Error",
        7 => "Non-recoverable error",
        8 => "Starting",
        9 => "Stopping",
        10 => "Stopped",
        17 => "Completed",
        _ => $"Status {code}",
    };

    // ================================================================================
    // #337: volume dirty bit.
    // ================================================================================

    private static readonly Regex DirtyRegex = new(@"is\s+(not\s+)?dirty", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>`fsutil dirty query &lt;vol&gt;` - "Volume C: is dirty" / "Volume C: is not dirty"
    /// (confirmed wording, Microsoft Learn's fsutil-dirty examples). Null on any failure, including
    /// the very common "Access is denied" this prints when the calling process isn't elevated -
    /// this app runs elevated (app.manifest requireAdministrator) so that's not expected in
    /// practice, but degrading to Unknown rather than guessing "not dirty" matters if it ever
    /// happens anyway.</summary>
    public static async Task<bool?> QueryDirtyAsync(string driveLetter)
    {
        var (exitCode, output) = await RunToolAsync("fsutil.exe", $"dirty query {driveLetter}:", 5000);
        if (exitCode != 0) return null;
        var match = DirtyRegex.Match(output);
        if (!match.Success) return null;
        return !match.Groups[1].Success; // group 1 present => "is NOT dirty"
    }

    // ================================================================================
    // #338: NTFS self-healing state.
    // ================================================================================

    // Confirmed wording: "Self healing is enabled for volume f: with flags 0x1" /
    // "Self healing is disabled for volume ...". `fsutil repair set` documents exactly three flag
    // values: 0x00 disabled, 0x01 enabled (repairs automatically), 0x09 warns about potential data
    // loss WITHOUT repairing - not a clean two-independent-bits model, so these are treated as three
    // discrete states rather than decoded bit-by-bit.
    private static readonly Regex RepairFlagsRegex = new(@"flags\s*0x([0-9A-Fa-f]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>`fsutil repair query &lt;vol&gt;` - degrades to (null, null, rawText) for a volume
    /// this doesn't apply to (non-NTFS) or any parse failure; never fabricates a state. WarnOnly
    /// true means "self-healing is off, but NTFS still logs/warns about corruptions it finds"
    /// (flags 0x09) - a real third state, not just "enabled" negated.</summary>
    public static async Task<(bool? SelfHealingEnabled, bool? WarnOnly, string RawText)> QueryRepairStateAsync(string driveLetter)
    {
        var (exitCode, output) = await RunToolAsync("fsutil.exe", $"repair query {driveLetter}:", 5000);
        string trimmed = output.Trim();
        if (exitCode != 0 || trimmed.Length == 0)
            return (null, null, trimmed.Length == 0 ? "No output." : trimmed);

        var match = RepairFlagsRegex.Match(trimmed);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int flags))
        {
            return flags switch
            {
                0 => (false, false, trimmed),
                1 => (true, false, trimmed),
                9 => (false, true, trimmed),
                _ => ((bool?)null, (bool?)null, trimmed), // an undocumented flag combination - show the raw text, don't guess
            };
        }

        // Fallback for wording that doesn't include "flags 0xN" - "disabled" checked first so a
        // "not enabled"-style phrasing (if any OS build uses one) doesn't false-positive as enabled.
        bool? enabled = trimmed.Contains("disabled", StringComparison.OrdinalIgnoreCase) ? false
            : trimmed.Contains("enabled", StringComparison.OrdinalIgnoreCase) ? true
            : null;
        return (enabled, null, trimmed);
    }

    /// <summary>`fsutil repair set &lt;vol&gt; {0|1}` - the two states Microsoft's own examples
    /// document (enable/disable); the third "warn only" (0x09) state isn't offered as a UI toggle
    /// here since there's no documented example of setting it, only of querying it.</summary>
    public static async Task<(bool Success, string Message)> SetRepairStateAsync(string driveLetter, bool enable)
    {
        var (exitCode, output) = await RunToolAsync("fsutil.exe", $"repair set {driveLetter}: {(enable ? 1 : 0)}", 5000);
        string trimmed = output.Trim();
        if (exitCode == 0)
            return (true, enable ? "NTFS self-healing enabled." : "NTFS self-healing disabled.");
        return (false, trimmed.Length > 0 ? trimmed : $"fsutil exited with code {exitCode}.");
    }

    // ================================================================================
    // #339: NTFS corruption record list.
    // ================================================================================

    /// <summary>`fsutil repair enumerate &lt;vol&gt; $Corrupt` - the confirmed corruption log (as
    /// opposed to `$Verify`'s unconfirmed/potential one), the justification for running the online
    /// scan below when non-empty.</summary>
    public static async Task<List<NtfsCorruptionRecord>> EnumerateCorruptionRecordsAsync(string driveLetter)
    {
        var (exitCode, output) = await RunToolAsync("fsutil.exe", $"repair enumerate {driveLetter}: $Corrupt", 15000);
        if (exitCode != 0) return new List<NtfsCorruptionRecord>();
        return ParseCorruptionRecords(output);
    }

    /// <summary>Microsoft's own fsutil-repair docs don't publish a worked example of this log's
    /// text, so this is a best-effort blank-line-separated paragraph split rather than a strict
    /// field-by-field parse - each non-empty paragraph (after stripping the fsutil banner/copyright
    /// lines and an explicit "no records"/"cannot find" empty-result line) becomes one record,
    /// Description verbatim. An empty result list is the normal, expected common case (no logged
    /// corruptions), not an error.</summary>
    private static List<NtfsCorruptionRecord> ParseCorruptionRecords(string rawOutput)
    {
        var result = new List<NtfsCorruptionRecord>();
        var paragraphs = Regex.Split(rawOutput.Replace("\r\n", "\n"), @"\n\s*\n")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Where(p => !p.StartsWith("Microsoft (R) Windows", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains("Copyright (c) Microsoft", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains("no records", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
            .ToList();

        for (int i = 0; i < paragraphs.Count; i++)
            result.Add(new NtfsCorruptionRecord { Index = i + 1, Description = paragraphs[i] });
        return result;
    }

    // ================================================================================
    // #343: boot-time chkdsk scheduling control.
    // ================================================================================

    /// <summary>`chkntfs &lt;vol&gt;` (requires an elevated process, per Microsoft's own docs - this
    /// app always runs elevated) plus the raw BootExecute value under
    /// HKLM\SYSTEM\CurrentControlSet\Control\Session Manager. IsExcluded is a best-effort read of
    /// that raw REG_MULTI_SZ text (looking for a "/k:&lt;letters&gt;" flag naming this drive) shown
    /// alongside chkntfs's own live answer, not in place of it, since the registry format for
    /// combining multiple excluded/scheduled drives isn't published with a worked example either.
    /// </summary>
    public static async Task<(string ChkntfsText, string BootExecuteText, bool IsExcluded)> QueryBootCheckStatusAsync(string driveLetter)
    {
        var (exitCode, output) = await RunToolAsync("chkntfs.exe", $"{driveLetter}:", 5000);
        string chkntfsText = output.Trim();
        if (chkntfsText.Length == 0)
            chkntfsText = exitCode == 0 ? "(no output)" : $"chkntfs exited with code {exitCode} (this app runs elevated, so this is more likely chkntfs being unavailable than a permissions issue).";

        string bootExecuteText = "Unknown";
        bool isExcluded = false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SessionManagerKeyPath);
            if (key?.GetValue("BootExecute") is string[] lines && lines.Length > 0)
            {
                bootExecuteText = string.Join("; ", lines);
                foreach (var line in lines)
                {
                    var kMatch = Regex.Match(line, @"/k:(\S+)", RegexOptions.IgnoreCase);
                    if (kMatch.Success && kMatch.Groups[1].Value.Contains(driveLetter, StringComparison.OrdinalIgnoreCase))
                    {
                        isExcluded = true;
                        break;
                    }
                }
            }
        }
        catch { /* registry read denied/unavailable - leave "Unknown" */ }

        return (chkntfsText, bootExecuteText, isExcluded);
    }

    /// <summary>Schedules a boot-time `chkdsk /f /r` on this volume by answering "Y" to the
    /// classic "Would you like to schedule this volume to be checked the next time the system
    /// restarts? (Y/N)" prompt - exactly what typing Y at that prompt does, and the reliable way to
    /// get autochk to run WITH /r (a full bad-sector scan) at next boot; the dirty bit alone
    /// (#337's `fsutil dirty set`) only gets a plain /f-equivalent pass.
    ///
    /// This only works cleanly when chkdsk can't lock the volume (the boot volume, or any volume
    /// with open handles) - it prompts, we answer, it exits in well under a second. If chkdsk CAN
    /// lock the volume (an unused data drive), it instead starts a real, possibly hours-long /r
    /// pass immediately instead of prompting at all. The short timeout+Kill() below exists
    /// specifically to catch that second case and stop it rather than block this app for hours -
    /// at the cost of possibly interrupting a real run that happened to start on its own.</summary>
    public static async Task<(bool Success, string Message)> ScheduleBootCheckAsync(string driveLetter)
    {
        var (exitCode, output) = await RunToolAsync("chkdsk.exe", $"{driveLetter}: /f /r", 10_000, stdin: "Y\r\n");
        string text = output.Trim();

        if (exitCode == -1 && text == "Timed out.")
            return (false, "chkdsk didn't prompt to schedule within 10 seconds - it most likely locked the volume and started a real check immediately instead (the volume wasn't in use), so it was stopped to avoid blocking the app for hours. This action is meant for a volume Windows can't lock right now (typically the boot volume); use the online scan or WMI repair actions above to check an unused data volume directly.");

        bool scheduled = text.Contains("scheduled", StringComparison.OrdinalIgnoreCase)
            || text.Contains("next time", StringComparison.OrdinalIgnoreCase)
            || text.Contains("next restart", StringComparison.OrdinalIgnoreCase);
        return (scheduled, text.Length > 0 ? text : (scheduled ? "Scheduled." : "chkdsk exited without a recognizable confirmation - see the raw output above."));
    }

    /// <summary>`chkntfs /x &lt;vol&gt;` - excludes the volume from the default boot-time check
    /// (cancels a pending scheduled/dirty-bit check for it). Per Microsoft's docs this isn't
    /// accumulative across drives in the way #343 might imply for multiple volumes - each call
    /// simply passes the one volume the user picked.</summary>
    public static async Task<(bool Success, string Message)> CancelBootCheckAsync(string driveLetter)
    {
        var (exitCode, output) = await RunToolAsync("chkntfs.exe", $"/x {driveLetter}:", 5000);
        string text = output.Trim();
        if (exitCode == 0) return (true, text.Length > 0 ? text : $"{driveLetter}: excluded from the boot-time check.");
        return (false, text.Length > 0 ? text : $"chkntfs exited with code {exitCode}.");
    }

    // ================================================================================
    // Round 16, #350: NTFS geometry facts.
    // ================================================================================

    // Confirmed field set/wording against Microsoft's own fsutil-fsinfo-ntfsinfo reference examples
    // - "Label : value" per line, values either decimal (the Bytes Per ... sector/cluster facts) or
    // 0x-prefixed hex (everything MFT-related). Parsed generically rather than a strict per-field
    // match so an older/newer build that reorders or adds lines (e.g. the Max Device/Volume Trim
    // lines some newer builds include) degrades gracefully instead of failing the whole read.
    private static readonly Regex NtfsInfoLineRegex = new(
        @"^(?<key>[A-Za-z$][A-Za-z0-9 $]*?)\s*:\s*(?<val>0[xX][0-9A-Fa-f]+|-?\d+)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>`fsutil fsinfo ntfsinfo &lt;vol&gt;` - NTFS-only (the command itself is NTFS-
    /// specific); callers gate this on VolumeFilesystemFacts.IsNtfs the same way they already gate
    /// #338/#339's repair-state/corruption-log reads.</summary>
    public static async Task<NtfsGeometryFacts> ReadGeometryFactsAsync(string driveLetter)
    {
        var (exitCode, output) = await RunToolAsync("fsutil.exe", $"fsinfo ntfsinfo {driveLetter}:", 8000);
        string trimmed = output.Trim();
        if (exitCode != 0)
            return new NtfsGeometryFacts { Available = false, UnavailableReason = trimmed.Length > 0 ? trimmed : $"fsutil exited with code {exitCode}.", RawText = trimmed };

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in NtfsInfoLineRegex.Matches(trimmed))
        {
            string key = Regex.Replace(m.Groups["key"].Value.Trim(), @"\s+", " ");
            values.TryAdd(key, m.Groups["val"].Value);
        }

        ulong? GetNumeric(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (!values.TryGetValue(k, out var raw)) continue;
                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (ulong.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hex)) return hex;
                }
                else if (ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong dec))
                {
                    return dec;
                }
            }
            return null;
        }

        ulong? bytesPerCluster = GetNumeric("Bytes Per Cluster");
        ulong? mftStartLcn = GetNumeric("Mft Start Lcn");
        if (bytesPerCluster is null && mftStartLcn is null)
            return new NtfsGeometryFacts { Available = false, UnavailableReason = trimmed.Length > 0 ? trimmed : "No recognizable ntfsinfo fields found.", RawText = trimmed };

        return new NtfsGeometryFacts
        {
            Available = true,
            BytesPerCluster = bytesPerCluster is { } bpc ? (uint)bpc : null,
            BytesPerSector = GetNumeric("Bytes Per Sector") is { } bps ? (uint)bps : null,
            BytesPerPhysicalSector = GetNumeric("Bytes Per Physical Sector") is { } bpps ? (uint)bpps : null,
            MftStartLcn = mftStartLcn,
            MftZoneStart = GetNumeric("Mft Zone Start"),
            MftZoneEnd = GetNumeric("Mft Zone End"),
            MftValidDataLengthBytes = GetNumeric("Mft Valid Data Length"),
            // Best-effort only - see NtfsGeometryFacts.LogFileSizeBytes's remarks. No build this was
            // tested against actually reports this field; the lookup is kept so a future/uncommon
            // build that does include it is picked up automatically rather than needing a code change.
            LogFileSizeBytes = GetNumeric("Log File Size", "LogFile Size", "$LogFile Size"),
            RawText = trimmed,
        };
    }

    // ================================================================================
    // Round 16, #349: NTFS metadata operation rates - raw cumulative counters only; StorageViewModel
    // derives per-second deltas from two samples taken a tick apart (see its remarks).
    // ================================================================================

    // Field names confirmed against Microsoft's own fsutil-fsinfo-statistics reference (NTFS File
    // System Statistics section) - "Label:value" tokens, several per line, no reliable per-line
    // structure to anchor on, so this scans the whole output for every "Word: number" token rather
    // than parsing line-by-line.
    private static readonly Regex StatisticsTokenRegex = new(@"([A-Za-z][A-Za-z0-9]*)\s*:\s*(-?\d+)", RegexOptions.Compiled);

    /// <summary>`fsutil fsinfo statistics &lt;vol&gt;` - NTFS-only. Available=false (not zeros) when
    /// none of the expected counters could be found at all, e.g. a non-NTFS volume or an
    /// unrecognized output shape on some future build.</summary>
    public static async Task<NtfsMetadataStatistics> ReadMetadataStatisticsAsync(string driveLetter)
    {
        var (exitCode, output) = await RunToolAsync("fsutil.exe", $"fsinfo statistics {driveLetter}:", 8000);
        string trimmed = output.Trim();
        if (exitCode != 0)
            return new NtfsMetadataStatistics { Available = false, UnavailableReason = trimmed.Length > 0 ? trimmed : $"fsutil exited with code {exitCode}." };

        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in StatisticsTokenRegex.Matches(trimmed))
        {
            if (long.TryParse(m.Groups[2].Value, out long v))
                values.TryAdd(m.Groups[1].Value, v);
        }

        long Get(params string[] keys)
        {
            foreach (var k in keys)
                if (values.TryGetValue(k, out var v)) return v;
            return 0;
        }

        if (values.Count == 0 ||
            (!values.ContainsKey("MftReads") && !values.ContainsKey("MetaDataReads") && !values.ContainsKey("LogFileWrites")))
        {
            return new NtfsMetadataStatistics { Available = false, UnavailableReason = trimmed.Length > 0 ? trimmed : "No recognizable statistics fields found." };
        }

        return new NtfsMetadataStatistics
        {
            Available = true,
            MftReads = Get("MftReads"),
            MftWrites = Get("MftWrites"),
            MetaDataReads = Get("MetaDataReads", "MetadataReads"),
            MetaDataWrites = Get("MetaDataWrites", "MetadataWrites"),
            LogFileWrites = Get("LogFileWrites"),
        };
    }

    // ================================================================================
    // Round 16, #351: NTFS behaviour settings audit - system-wide (not per volume), so this reads
    // once for the whole machine rather than once per VolumeFilesystemRow.
    // ================================================================================

    /// <summary>Key, display label, and one-line performance implication for each of the five
    /// settings this card audits - CanToggle picks disablelastaccess/disable8dot3 as "the two most
    /// commonly toggled and safest to expose", per this round's brief; mftzone/memoryusage/
    /// encryptpagingfile stay read-only since their tradeoffs are more nuanced than a naive on/off.</summary>
    private static readonly (string Key, string Label, string Implication, bool CanToggle)[] BehaviorSettingDefs =
    {
        ("disablelastaccess", "Last-access-time updates",
            "Disabling last-access-time updates skips a metadata write on every file read - a common performance tweak, at the cost of losing last-accessed-time info some backup/cleanup/audit tools rely on.",
            true),
        ("disable8dot3", "8.3 (short) filename creation",
            "Disabling 8.3 short-name generation skips a metadata write on every file/folder create in large directories - a common performance tweak, but breaks any legacy 16-bit/older app that expects a short name.",
            true),
        ("mftzone", "MFT zone reservation size",
            "A larger MFT zone reservation reduces MFT fragmentation as the volume fills with many files, at the cost of reserving more space up front that user data can't use until the MFT grows into it.",
            false),
        ("memoryusage", "NTFS metadata cache memory usage",
            "Raising this lets NTFS cache more metadata in memory, which can help workloads with huge directories or many small files, at the cost of memory available to everything else.",
            false),
        ("encryptpagingfile", "Paging file encryption",
            "Encrypting the paging file protects data swapped out of memory from being read back off disk, at the cost of a small overhead on every page-in/page-out.",
            false),
    };

    /// <summary>`fsutil behavior query &lt;setting&gt;` for each of the five settings above - wording
    /// genuinely differs between them (confirmed live: disablelastaccess/mftzone/memoryusage/
    /// encryptpagingfile all read "Name = value  (description)", while disable8dot3 reads "The
    /// registry state is: value (description)"), so the raw trimmed text is always shown verbatim
    /// rather than reduced to a single guessed value.</summary>
    public static async Task<List<NtfsBehaviorSetting>> QueryBehaviorSettingsAsync()
    {
        var result = new List<NtfsBehaviorSetting>();
        foreach (var (key, label, implication, canToggle) in BehaviorSettingDefs)
        {
            var (exitCode, output) = await RunToolAsync("fsutil.exe", $"behavior query {key}", 5000);
            string trimmed = output.Trim();
            string valueText = trimmed.Length > 0
                ? Regex.Replace(trimmed, @"\s*\r?\n\s*", " ").Trim()
                : (exitCode == 0 ? "(no output)" : $"fsutil exited with code {exitCode}.");

            result.Add(new NtfsBehaviorSetting
            {
                Key = key,
                Label = label,
                ValueText = valueText,
                PerformanceImplication = implication,
                CanToggle = canToggle,
            });
        }
        return result;
    }

    /// <summary>`fsutil behavior set &lt;setting&gt; &lt;value&gt;` - both toggle controls in the UI
    /// only ever pass the simple, documented 0 (enable) / 1 (disable) values, never the more nuanced
    /// per-volume/system-managed values some of these settings also support.</summary>
    public static async Task<(bool Success, string Message)> SetBehaviorAsync(string key, int value)
    {
        var (exitCode, output) = await RunToolAsync("fsutil.exe", $"behavior set {key} {value}", 5000);
        string trimmed = output.Trim();
        if (exitCode == 0) return (true, trimmed.Length > 0 ? trimmed : "Set. This typically takes effect after a reboot.");
        return (false, trimmed.Length > 0 ? trimmed : $"fsutil exited with code {exitCode}.");
    }

    // ================================================================================
    // Shared shell-out helper - see the class remarks for why this is centralized in this file.
    // ================================================================================

    private static async Task<(int ExitCode, string Output)> RunToolAsync(string exe, string args, int timeoutMs, string? stdin = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (-1, $"Couldn't start {exe}.");

            if (stdin is not null)
            {
                try
                {
                    await proc.StandardInput.WriteAsync(stdin);
                    proc.StandardInput.Close();
                }
                catch { /* best-effort - if the process never reads stdin (didn't prompt), this is harmless */ }
            }

            // Concurrent async reads + a bounded WaitForExitAsync + Kill()-on-timeout - the same
            // pattern VolumeDiagnosticsService/DiskFragmentationService/BadSectorService/
            // ClusterMappingService each already use (see VolumeDiagnosticsService's remarks for why
            // the read/wait ordering matters).
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
                return (-1, "Timed out.");
            }

            string output = (await outputTask) + (await errorTask);
            return (proc.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, $"Failed: {ex.Message}");
        }
    }
}
