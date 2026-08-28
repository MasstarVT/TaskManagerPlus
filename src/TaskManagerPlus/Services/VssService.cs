using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 21 (final chunk), #394-#400: the Storage tab's "Volume Shadow Copy" card - VSS writer
/// health (#394), correlated VSS/SPP/volsnap failure events (#395/#399), shadow copy inventory
/// (#396), shadow storage allocation/limit + resize (#397), restore point list + System
/// Protection per-drive state + "create a restore point now" (#398), and registered VSS provider
/// inventory (#400).
///
/// Every `vssadmin` call here follows the same concurrent-read + bounded-WaitForExitAsync +
/// Kill()-on-timeout shell-out shape VolumeDiagnosticsService's fsutil/vssadmin calls already use
/// (see RunVssAdminAsync). #397 folds the "Used/Allocated/Maximum Shadow Copy Storage space"
/// figures into the SAME `vssadmin list shadowstorage` parse VolumeDiagnosticsService already ran
/// for #42's aggregate-bytes-used card, rather than shelling out to the same command twice - this
/// class now owns that one read (ReadShadowStorageAsync), and
/// VolumeDiagnosticsService.ReadShadowCopyUsageByVolumeAsync delegates to it, keeping its existing
/// signature/caller (SystemSpecsService.ReadVolumesAsync) unchanged.
///
/// Degrades to "no writers/shadows/providers found" / Unknown rather than throwing throughout -
/// an empty writer list, no shadow copies at all, and System Restore being off system-wide are
/// all normal, expected states on plenty of real machines, not bugs.
/// </summary>
public static class VssService
{
    // ================================================================================
    // Shared vssadmin shell-out - concurrent async reads + bounded WaitForExitAsync +
    // Kill()-on-timeout, the same pattern VolumeDiagnosticsService.ReadShadowCopyUsageByVolumeAsync
    // and ReclaimableSpaceService.RunProcessAsync already use.
    // ================================================================================
    private static async Task<string> RunVssAdminAsync(string arguments, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo("vssadmin.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return string.Empty;

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
                return string.Empty;
            }

            return (await outputTask) + (await errorTask);
        }
        catch
        {
            // vssadmin.exe missing/unavailable, or a launch failure - degrade to "nothing read"
            // like every other shell-out in this app.
            return string.Empty;
        }
    }

    private static double UnitMultiplier(string unit) => unit.ToUpperInvariant() switch
    {
        "BYTES" => 1,
        "KB" => 1024,
        "MB" => 1024d * 1024,
        "GB" => 1024d * 1024 * 1024,
        "TB" => 1024d * 1024 * 1024 * 1024,
        _ => 1,
    };

    private static long ParseBytes(Match numberUnitMatch)
    {
        if (!double.TryParse(numberUnitMatch.Groups[1].Value, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out double amount))
            return 0;
        return (long)(amount * UnitMultiplier(numberUnitMatch.Groups[2].Value));
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";

    // ================================================================================
    // #394: VSS writer health - `vssadmin list writers`. Blocks look like:
    //   Writer name: 'Task Scheduler Writer'
    //      Writer Id: {guid}
    //      Writer Instance Id: {guid}
    //      State: [1] Stable
    //      Last error: No error
    // ================================================================================
    private static readonly Regex WriterNameRegex = new(@"Writer name:\s*'([^']*)'", RegexOptions.Compiled);
    private static readonly Regex WriterInstanceIdRegex = new(@"Writer Instance Id:\s*(\{[0-9a-fA-F-]+\})", RegexOptions.Compiled);
    private static readonly Regex WriterIdRegex = new(@"Writer Id:\s*(\{[0-9a-fA-F-]+\})", RegexOptions.Compiled);
    private static readonly Regex WriterStateRegex = new(@"State:\s*\[(\d+)\]\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex WriterLastErrorRegex = new(@"Last error:\s*(.+)", RegexOptions.Compiled);

    public static async Task<List<VssWriterInfo>> ReadWritersAsync()
    {
        var result = new List<VssWriterInfo>();
        string output = await RunVssAdminAsync("list writers");
        if (output.Length == 0) return result;

        VssWriterInfo? current = null;
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            var nameMatch = WriterNameRegex.Match(line);
            if (nameMatch.Success)
            {
                if (current is not null) result.Add(current);
                current = new VssWriterInfo { Name = nameMatch.Groups[1].Value };
                continue;
            }
            if (current is null) continue;

            // Instance Id checked before the plain Id regex - "Writer Instance Id:" doesn't
            // contain the literal substring "Writer Id:" so the two never collide, but checking
            // the more specific one first keeps this order-independent regardless.
            var instanceMatch = WriterInstanceIdRegex.Match(line);
            if (instanceMatch.Success) { current.InstanceId = instanceMatch.Groups[1].Value; continue; }

            var idMatch = WriterIdRegex.Match(line);
            if (idMatch.Success) { current.Id = idMatch.Groups[1].Value; continue; }

            var stateMatch = WriterStateRegex.Match(line);
            if (stateMatch.Success)
            {
                current.StateCode = int.TryParse(stateMatch.Groups[1].Value, out int code) ? code : -1;
                current.StateText = stateMatch.Groups[2].Value.Trim();
                continue;
            }

            var errorMatch = WriterLastErrorRegex.Match(line);
            if (errorMatch.Success) { current.LastError = errorMatch.Groups[1].Value.Trim(); continue; }
        }
        if (current is not null) result.Add(current);

        return result;
    }

    // ================================================================================
    // #395/#399: correlated VSS/SPP/volsnap failure events - Application-log "VSS" (8193, 12289,
    // 12293) and "SPP" (16387), System-log "volsnap" (25, 33, 36). Same EventLogQuery/
    // EventLogReader shape as DiskDiagnosisEventService/NtfsCorruptionEventService, degrading to
    // empty per source rather than throwing - a provider that's simply never logged anything is
    // the common, healthy-system case.
    // ================================================================================
    private static readonly (string LogName, string Provider, int EventId)[] EventSources =
    {
        ("Application", "VSS", 8193),
        ("Application", "VSS", 12289),
        ("Application", "VSS", 12293),
        ("Application", "SPP", 16387),
        ("System", "volsnap", 25),
        ("System", "volsnap", 33),
        ("System", "volsnap", 36),
    };

    private const int EventLookbackDays = 30;
    private const int MaxEventsPerSource = 50;
    private const int MaxEventsTotal = 200;

    // Fallback for messages that mention a drive letter directly ("...volume C:...") rather than
    // a \Device\HarddiskVolumeN path - volsnap's wording varies by Windows version and this
    // catches the common "volume X:" phrasing DevicePathResolver's device-path regex can't.
    private static readonly Regex DriveLetterInMessageRegex = new(@"\bvolume\s+([A-Za-z]):", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<VssRelatedEventInfo> ReadRelatedEvents()
    {
        var deviceToLetter = DevicePathResolver.BuildDeviceToLetterMap();
        var events = new List<VssRelatedEventInfo>();

        foreach (var (logName, provider, eventId) in EventSources)
            ReadOneEventSource(events, logName, provider, eventId, deviceToLetter);

        return events.OrderByDescending(e => e.TimeCreated).Take(MaxEventsTotal).ToList();
    }

    private static void ReadOneEventSource(List<VssRelatedEventInfo> into, string logName, string providerName, int eventId, Dictionary<string, string> deviceToLetter)
    {
        try
        {
            long maxAgeMs = EventLookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery(logName, PathType.LogName,
                $"*[System[Provider[@Name='{providerName}'] and EventID={eventId} and TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxEventsPerSource && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    into.Add(new VssRelatedEventInfo
                    {
                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                        Source = providerName,
                        EventId = eventId,
                        Volume = ResolveVolumeText(message, deviceToLetter),
                        Message = Truncate(message, 300),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable, or (the common case) this event has simply never fired -
            // contribute nothing for this source.
        }
    }

    private static string ResolveVolumeText(string message, Dictionary<string, string> deviceToLetter)
    {
        string viaDevice = DevicePathResolver.ResolveVolumeFromMessage(message, deviceToLetter);
        if (viaDevice != "Unknown volume") return viaDevice;

        var m = DriveLetterInMessageRegex.Match(message);
        return m.Success ? $"{m.Groups[1].Value.ToUpperInvariant()}:" : "Unknown volume";
    }

    // ================================================================================
    // #396: shadow copy inventory - `vssadmin list shadows`. Blocks look like:
    //   Contents of shadow copy set ID: {guid}
    //      Contained 1 shadow copies at creation time: 8/27/2026 3:00:15 AM
    //         Shadow Copy ID: {guid}
    //            Original Volume: (C:)\\?\Volume{guid}\
    //            Shadow Copy Volume: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1
    //            Originating Machine: MACHINE
    //            Service Machine: MACHINE
    //            Provider: 'Microsoft Software Shadow Copy provider 1.0'
    //            Type: ClientAccessible
    // ================================================================================
    private static readonly Regex ShadowSetIdRegex = new(@"shadow copy set ID:\s*(\{[0-9a-fA-F-]+\})", RegexOptions.Compiled);
    private static readonly Regex ContainedAtRegex = new(@"Contained\s+\d+\s+shadow cop(?:y|ies) at creation time:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex ShadowCopyIdRegex = new(@"Shadow Copy ID:\s*(\{[0-9a-fA-F-]+\})", RegexOptions.Compiled);
    private static readonly Regex OriginalVolumeRegex = new(@"Original Volume:.*\(([A-Za-z]):\)", RegexOptions.Compiled);
    private static readonly Regex ShadowCopyVolumeRegex = new(@"Shadow Copy Volume:\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex OriginatingMachineRegex = new(@"Originating Machine:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex ServiceMachineRegex = new(@"Service Machine:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex ShadowProviderRegex = new(@"Provider:\s*'([^']*)'", RegexOptions.Compiled);

    public static async Task<List<VssShadowCopyInfo>> ReadShadowCopiesAsync()
    {
        var result = new List<VssShadowCopyInfo>();
        string output = await RunVssAdminAsync("list shadows");
        if (output.Length == 0) return result;

        string? currentSetId = null;
        DateTime? currentCreationTime = null;
        VssShadowCopyInfo? current = null;

        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            var setMatch = ShadowSetIdRegex.Match(line);
            if (setMatch.Success) { currentSetId = setMatch.Groups[1].Value; continue; }

            var containedMatch = ContainedAtRegex.Match(line);
            if (containedMatch.Success)
            {
                currentCreationTime = DateTime.TryParse(containedMatch.Groups[1].Value.Trim(),
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Globalization.DateTimeStyles.None, out var dt) ? dt : null;
                continue;
            }

            var idMatch = ShadowCopyIdRegex.Match(line);
            if (idMatch.Success)
            {
                if (current is not null) result.Add(current);
                current = new VssShadowCopyInfo
                {
                    ShadowCopyId = idMatch.Groups[1].Value,
                    ShadowCopySetId = currentSetId ?? string.Empty,
                    CreationTime = currentCreationTime,
                };
                continue;
            }
            if (current is null) continue;

            var volMatch = OriginalVolumeRegex.Match(line);
            if (volMatch.Success) { current.Volume = $"{volMatch.Groups[1].Value.ToUpperInvariant()}:"; continue; }

            var scVolMatch = ShadowCopyVolumeRegex.Match(line);
            if (scVolMatch.Success) { current.ShadowCopyVolume = scVolMatch.Groups[1].Value.Trim(); continue; }

            var originMatch = OriginatingMachineRegex.Match(line);
            if (originMatch.Success) { current.OriginatingMachine = originMatch.Groups[1].Value.Trim(); continue; }

            var serviceMatch = ServiceMachineRegex.Match(line);
            if (serviceMatch.Success) { current.ServiceMachine = serviceMatch.Groups[1].Value.Trim(); continue; }

            var providerMatch = ShadowProviderRegex.Match(line);
            if (providerMatch.Success) { current.Provider = providerMatch.Groups[1].Value.Trim(); continue; }
        }
        if (current is not null) result.Add(current);

        // Grouped by volume (per this item's brief), most-recent-first within each volume.
        return result
            .OrderBy(s => s.Volume, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(s => s.CreationTime)
            .ToList();
    }

    // ================================================================================
    // #397: shadow storage allocation/limit - `vssadmin list shadowstorage`. THE SAME command
    // VolumeDiagnosticsService (#42) already parsed for just "Used Shadow Copy Storage space" -
    // this is that parse, extended to also carry Allocated/Maximum. Blocks look like:
    //   Shadow Copy Storage association
    //      For volume: (C:)\\?\Volume{guid}\
    //      Shadow Copy Storage volume: (C:)\\?\Volume{guid}\
    //      Used Shadow Copy Storage space: 7.926 GB (0%)
    //      Allocated Shadow Copy Storage space: 7.941 GB (0%)
    //      Maximum Shadow Copy Storage space: 33.749 GB (10%)      <- or "UNBOUNDED"
    // ================================================================================
    private static readonly Regex ShadowStorageForVolumeRegex = new(@"For volume:.*\(([A-Za-z]):\)", RegexOptions.Compiled);
    private static readonly Regex ShadowStorageStorageVolumeRegex = new(@"Shadow Copy Storage volume:.*\(([A-Za-z]):\)", RegexOptions.Compiled);
    private static readonly Regex ShadowStorageUsedRegex = new(@"Used Shadow Copy Storage space:\s*([\d.,]+)\s*(bytes|KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ShadowStorageAllocatedRegex = new(@"Allocated Shadow Copy Storage space:\s*([\d.,]+)\s*(bytes|KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ShadowStorageMaximumRegex = new(@"Maximum Shadow Copy Storage space:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NumberUnitRegex = new(@"([\d.,]+)\s*(bytes|KB|MB|GB|TB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The one and only `vssadmin list shadowstorage` shell-out in the app -
    /// VolumeDiagnosticsService.ReadShadowCopyUsageByVolumeAsync delegates here for its
    /// used-bytes-only projection rather than running this command a second time.</summary>
    public static async Task<List<VssShadowStorageInfo>> ReadShadowStorageAsync()
    {
        var result = new List<VssShadowStorageInfo>();
        string output = await RunVssAdminAsync("list shadowstorage");
        if (output.Length == 0) return result;

        VssShadowStorageInfo? current = null;
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (line.Contains("Shadow Copy Storage association", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null) result.Add(current);
                current = new VssShadowStorageInfo();
                continue;
            }
            if (current is null) continue;

            var forMatch = ShadowStorageForVolumeRegex.Match(line);
            if (forMatch.Success) { current.Volume = $"{forMatch.Groups[1].Value.ToUpperInvariant()}:"; continue; }

            var storageVolMatch = ShadowStorageStorageVolumeRegex.Match(line);
            if (storageVolMatch.Success) { current.StorageVolume = $"{storageVolMatch.Groups[1].Value.ToUpperInvariant()}:"; continue; }

            var usedMatch = ShadowStorageUsedRegex.Match(line);
            if (usedMatch.Success) { current.UsedBytes = ParseBytes(usedMatch); continue; }

            var allocMatch = ShadowStorageAllocatedRegex.Match(line);
            if (allocMatch.Success) { current.AllocatedBytes = ParseBytes(allocMatch); continue; }

            var maxMatch = ShadowStorageMaximumRegex.Match(line);
            if (maxMatch.Success)
            {
                string text = maxMatch.Groups[1].Value.Trim();
                if (text.StartsWith("UNBOUNDED", StringComparison.OrdinalIgnoreCase))
                {
                    current.IsUnbounded = true;
                    current.MaximumBytes = null;
                }
                else
                {
                    var numMatch = NumberUnitRegex.Match(text);
                    current.MaximumBytes = numMatch.Success ? ParseBytes(numMatch) : null;
                    current.IsUnbounded = false;
                }
                continue;
            }
        }
        if (current is not null) result.Add(current);

        return result;
    }

    /// <summary>#397: `vssadmin resize shadowstorage /for=&lt;vol&gt; /on=&lt;vol&gt;
    /// /maxsize=&lt;NN&gt;GB` (or /maxsize=UNBOUNDED). Caller (StorageViewModel) confirms with the
    /// user first, same Yes/No MessageBox.Show pattern this app's other disruptive
    /// actions (chkdsk repair, USN journal delete, ...) already use - shrinking below the current
    /// usage makes Windows delete older shadow copies on the volume to fit.</summary>
    public static async Task<(bool Success, string Message)> ResizeShadowStorageAsync(string forVolume, string onVolume, string maxSizeArg)
    {
        string args = $"resize shadowstorage /for={forVolume} /on={onVolume} /maxsize={maxSizeArg}";
        string output = await RunVssAdminAsync(args, 20000);
        if (output.Length == 0) return (false, "vssadmin did not respond (timed out, or failed to start).");

        if (output.Contains("Successfully resized", StringComparison.OrdinalIgnoreCase))
            return (true, "Shadow copy storage resized successfully.");

        var errorLine = output.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("Error:", StringComparison.OrdinalIgnoreCase));
        return (false, errorLine ?? Truncate(output.Trim(), 300));
    }

    // ================================================================================
    // #398: restore points - WMI SystemRestore (root\default).
    // ================================================================================
    public static List<RestorePointInfo> ReadRestorePoints()
    {
        var result = new List<RestorePointInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT * FROM SystemRestore");
            foreach (ManagementObject rp in searcher.Get())
            {
                try
                {
                    DateTime? created = null;
                    if (rp["CreationTime"] is string wmiTime && wmiTime.Length > 0)
                    {
                        try { created = ManagementDateTimeConverter.ToDateTime(wmiTime); }
                        catch { /* leave null rather than a fabricated time */ }
                    }

                    int typeCode = rp["RestorePointType"] is null ? -1 : Convert.ToInt32(rp["RestorePointType"]);
                    result.Add(new RestorePointInfo
                    {
                        SequenceNumber = rp["SequenceNumber"] is null ? 0 : Convert.ToInt32(rp["SequenceNumber"]),
                        CreationTime = created,
                        Description = rp["Description"] as string ?? string.Empty,
                        RestorePointTypeText = RestorePointTypeText(typeCode),
                    });
                }
                catch { /* one malformed instance shouldn't stop the rest of the scan */ }
                finally { rp.Dispose(); }
            }
        }
        catch
        {
            // root\default\SystemRestore unavailable - System Protection is off system-wide, this
            // Windows edition doesn't expose the class, or WMI access is denied. Degrades to an
            // empty list, same tier as every other WMI read in this app; an empty list on a drive
            // the user believes is protected is itself the finding, per this item's brief.
        }
        return result.OrderByDescending(r => r.CreationTime ?? DateTime.MinValue).ToList();
    }

    /// <summary>Microsoft's own documented SystemRestore.RestorePointType enum values - every
    /// documented value decoded, not just the common ones, since all of them are genuinely
    /// documented (no guessed labels for an undocumented code).</summary>
    private static string RestorePointTypeText(int code) => code switch
    {
        0 => "Application install",
        1 => "Application uninstall",
        10 => "Device driver install",
        12 => "Modify settings",
        13 => "Cancelled operation",
        _ => code < 0 ? "Unknown" : $"Type {code}",
    };

    /// <summary>#398: creates a restore point right now. Shells out to `powershell.exe
    /// Checkpoint-Computer` rather than calling SystemRestore.CreateRestorePoint's WMI method
    /// directly - that WMI method is documented to need a Windows message pump on the calling
    /// thread internally (SrClient) and is known to hang/misbehave when invoked from a plain
    /// background thread with no pump, which is exactly how every other WMI call in this app is
    /// made (Task.Run). Checkpoint-Computer avoids that pitfall entirely and is the same "known
    /// tool over a fragile direct call" tradeoff this app already takes elsewhere. NOTE: Windows
    /// throttles restore-point creation to once per SystemRestorePointCreationFrequency (1440
    /// minutes / 24 hours by default) for non-critical types - Checkpoint-Computer still exits
    /// successfully in that case, it just doesn't add a new point, so the caller (StorageViewModel)
    /// cross-checks the restore-point count before/after and calls that out explicitly.</summary>
    public static async Task<(bool Success, string Message)> CreateRestorePointAsync(string description)
    {
        string trimmed = string.IsNullOrWhiteSpace(description) ? "Task Manager Plus" : description.Trim();
        string safeDescription = trimmed.Replace("'", "''");

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Checkpoint-Computer -Description '{safeDescription}' -RestorePointType MODIFY_SETTINGS\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "Couldn't start powershell.exe.");

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            var errorTask = proc.StandardError.ReadToEndAsync();

            // Checkpoint-Computer can genuinely take a while (it waits on the VSS writers) - a
            // longer timeout than the read-only vssadmin queries above.
            using var cts = new CancellationTokenSource(90_000);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                return (false, "Timed out waiting for Checkpoint-Computer.");
            }

            string output = ((await outputTask) + (await errorTask)).Trim();
            bool looksLikeFailure = output.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                || output.Contains("Error", StringComparison.OrdinalIgnoreCase)
                || output.Contains("cannot be loaded", StringComparison.OrdinalIgnoreCase);

            if (proc.ExitCode == 0 && !looksLikeFailure)
                return (true, "Checkpoint-Computer completed.");

            return (false, output.Length > 0 ? Truncate(output, 400) : $"Checkpoint-Computer exited with code {proc.ExitCode}.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed: {ex.Message}");
        }
    }

    // ================================================================================
    // #398: System Protection's per-drive enabled state.
    // ================================================================================
    private const string SppClientsKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients";

    // The undocumented-but-well-established client GUID Windows itself files System Restore's
    // per-volume enrollment under in the key above (a REG_MULTI_SZ of "\\?\Volume{guid}\" paths).
    private const string SystemRestoreClientGuid = "{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string lpszVolumeMountPoint, StringBuilder lpszVolumeName, uint cchBufferLength);

    /// <summary>Primary detection: the SPP\Clients registry value above, matched per-drive via a
    /// volume-GUID-path lookup (GetVolumeNameForVolumeMountPointW - the standard, minimal Win32
    /// call for this; there's no documented WMI class or CLI tool that maps a drive letter to its
    /// volume GUID path directly, so this is one of this app's few raw-interop cases, reserved for
    /// exactly that "no tool/WMI class available" situation per CLAUDE.md, same as
    /// DevicePathResolver's QueryDosDeviceW call). Falls back per-drive to the #397
    /// shadow-storage-association proxy this item's brief explicitly sanctions when the registry
    /// read isn't available or the GUID-path lookup fails for that drive - DetectionMethodText
    /// always says which was used.</summary>
    public static List<SystemProtectionDriveStatus> ReadSystemProtectionStatus(List<VssShadowStorageInfo> shadowStorageEntries)
    {
        var result = new List<SystemProtectionDriveStatus>();

        string[]? protectedVolumeGuidPaths = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(SppClientsKeyPath);
            if (key?.GetValue(SystemRestoreClientGuid) is string[] values)
                protectedVolumeGuidPaths = values;
        }
        catch
        {
            // Access denied, or the key/value simply doesn't exist on this Windows version - every
            // drive below falls back to the shadow-storage-association proxy.
        }

        var shadowStorageVolumes = new HashSet<string>(
            shadowStorageEntries.Where(e => !string.IsNullOrEmpty(e.Volume)).Select(e => e.Volume),
            StringComparer.OrdinalIgnoreCase);

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            string letter = drive.Name.TrimEnd('\\');

            bool? viaRegistry = null;
            if (protectedVolumeGuidPaths is not null)
            {
                try
                {
                    var buffer = new StringBuilder(64);
                    if (GetVolumeNameForVolumeMountPointW(drive.Name, buffer, (uint)buffer.Capacity))
                    {
                        string guidPath = buffer.ToString();
                        viaRegistry = protectedVolumeGuidPaths.Any(p => p.Equals(guidPath, StringComparison.OrdinalIgnoreCase));
                    }
                }
                catch { /* leave null - falls back to the proxy below for this drive */ }
            }

            bool isProtected;
            string method;
            if (viaRegistry is { } vr)
            {
                isProtected = vr;
                method = "Detected via the SPP\\Clients registry value (System Restore's client enrollment list).";
            }
            else
            {
                isProtected = shadowStorageVolumes.Contains(letter);
                method = "Detected via proxy: this volume has an active shadow-copy-storage association (#397) - " +
                    "the SPP\\Clients registry value wasn't available for a definitive per-drive read on this system.";
            }

            result.Add(new SystemProtectionDriveStatus
            {
                DriveLetter = letter,
                IsProtected = isProtected,
                DetectionMethodText = method,
            });
        }

        return result;
    }

    // ================================================================================
    // #400: VSS provider inventory - `vssadmin list providers`. Blocks look like:
    //   Provider name: 'Microsoft Software Shadow Copy provider 1.0'
    //      Provider type: System
    //      Provider Id: {guid}
    //      Version: 1.0.0.7
    // ================================================================================
    private static readonly Regex ProviderNameRegex = new(@"Provider name:\s*'([^']*)'", RegexOptions.Compiled);
    private static readonly Regex ProviderTypeRegex = new(@"Provider type:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex ProviderIdRegex = new(@"Provider Id:\s*(\{[0-9a-fA-F-]+\})", RegexOptions.Compiled);
    private static readonly Regex ProviderVersionRegex = new(@"Version:\s*(.+)", RegexOptions.Compiled);

    public static async Task<List<VssProviderInfo>> ReadProvidersAsync()
    {
        var result = new List<VssProviderInfo>();
        string output = await RunVssAdminAsync("list providers");
        if (output.Length == 0) return result;

        VssProviderInfo? current = null;
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            var nameMatch = ProviderNameRegex.Match(line);
            if (nameMatch.Success)
            {
                if (current is not null) result.Add(current);
                current = new VssProviderInfo { Name = nameMatch.Groups[1].Value };
                continue;
            }
            if (current is null) continue;

            var typeMatch = ProviderTypeRegex.Match(line);
            if (typeMatch.Success) { current.TypeText = typeMatch.Groups[1].Value.Trim(); continue; }

            var idMatch = ProviderIdRegex.Match(line);
            if (idMatch.Success) { current.Id = idMatch.Groups[1].Value; continue; }

            var versionMatch = ProviderVersionRegex.Match(line);
            if (versionMatch.Success) { current.Version = versionMatch.Groups[1].Value.Trim(); continue; }
        }
        if (current is not null) result.Add(current);

        return result;
    }
}
