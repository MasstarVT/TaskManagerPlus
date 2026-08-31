using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>The three MSFT_Volume.Repair modes Repair-Volume exposes - see RunVolumeRepairAsync.</summary>
public enum VolumeRepairMode { Scan, SpotFix, OfflineScanAndFix }

/// <summary>
/// Round 15, #340/#341/#342: the online chkdsk /scan runner (streamed line-by-line, cancellable),
/// the MSFT_Volume.Repair WMI action (Scan/SpotFix/OfflineScanAndFix), a small persisted
/// "last scanned" history for #340/#341's own runs, and a read of the chkdsk reports Windows itself
/// already logs (Wininit event 1001 at boot, the classic "Chkdsk" provider's online-run events for
/// #342). All on-demand - see StorageViewModel for the triggering commands.
/// </summary>
public static class ChkdskService
{
    // ================================================================================
    // #340: online chkdsk /scan runner - event-based streaming (OutputDataReceived/
    // BeginOutputReadLine), not the buffered ReadToEndAsync pattern every other shell-out in this
    // app uses, because this specifically needs live line-by-line output in a scrollable pane
    // rather than one final string - per this round's brief.
    // ================================================================================

    /// <summary>Runs `chkdsk &lt;vol&gt; /scan` (NTFS online verification - no dismount, no
    /// reboot), streaming each output line to <paramref name="onLine"/> as it arrives. Cancellable:
    /// cancelling <paramref name="token"/> kills the process rather than waiting for it to finish.
    /// <paramref name="onLine"/> is invoked on a background thread-pool thread (the Process class's
    /// own async-read callback thread), same as SurfaceScanService/FileVerificationService's
    /// progress callbacks - the caller (StorageViewModel) marshals to the UI thread itself.</summary>
    public static async Task<(bool ProblemsFound, string Verdict)> RunOnlineScanAsync(string driveLetter, Action<string> onLine, CancellationToken token)
    {
        var psi = new ProcessStartInfo("chkdsk.exe", $"{driveLetter}: /scan")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var buffer = new StringBuilder();
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        void Handle(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            buffer.AppendLine(e.Data);
            onLine(e.Data);
        }

        proc.OutputDataReceived += Handle;
        proc.ErrorDataReceived += Handle;

        if (!proc.Start())
            return (false, "Couldn't start chkdsk.exe.");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Kill on cancel rather than waiting for chkdsk to finish on its own - a /scan pass can run
        // for minutes on a large volume.
        using var registration = token.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ } });
        try
        {
            await proc.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort - registration above likely already did this */ }
            throw;
        }

        return ParseScanVerdict(buffer.ToString());
    }

    private static readonly Regex ScanBadSectorsKbRegex = new(@"(\d[\d,]*)\s*KB in bad sectors", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>chkdsk's own console wording for a clean /scan varies slightly across Windows
    /// versions ("found no problems" / "found no further problems") - checked first since it's the
    /// unambiguous common case; a bad-sectors KB figure or "found problems" wording otherwise marks
    /// a real finding. Falls back to a stated "no clear verdict text found" rather than guessing
    /// either way when neither pattern matches.</summary>
    private static (bool ProblemsFound, string Verdict) ParseScanVerdict(string output)
    {
        if (output.Contains("found no problems", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("found no further problems", StringComparison.OrdinalIgnoreCase))
            return (false, "Windows scanned the file system and found no problems.");

        var kbMatch = ScanBadSectorsKbRegex.Match(output);
        if (kbMatch.Success)
            return (true, $"Problems found - {kbMatch.Groups[1].Value} KB in bad sectors (see the log above for detail).");

        if (output.Contains("found problems", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("has made corrections", StringComparison.OrdinalIgnoreCase))
            return (true, "Problems found - see the log above for detail.");

        return (false, "Scan completed - no clear verdict text was found in the output; see the log above.");
    }

    // ================================================================================
    // #340/#341: small persisted "last scanned" history for this app's own runs -
    // chkdsk-history.json under AppPaths.SettingsDirectory, same fail-silent-to-defaults shape as
    // SmartHistoryService/smart-history.json.
    // ================================================================================

    private static string HistoryPath => AppPaths.GetPath("chkdsk-history.json");
    private static readonly object HistoryLock = new();

    public static List<ChkdskScanRecord> LoadScanHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var json = File.ReadAllText(HistoryPath);
                var entries = JsonSerializer.Deserialize<List<ChkdskScanRecord>>(json);
                if (entries is not null) return entries;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file - start fresh, same as every other settings load in this app.
        }
        return new List<ChkdskScanRecord>();
    }

    /// <summary>Most recent app-initiated result for one drive letter, for the card's
    /// "last scanned: &lt;date&gt; - ..." line.</summary>
    public static ChkdskScanRecord? LastScanFor(string driveLetter) =>
        LoadScanHistory().Where(e => e.DriveLetter.Equals(driveLetter, StringComparison.OrdinalIgnoreCase))
                          .OrderByDescending(e => e.Timestamp).FirstOrDefault();

    public static void AppendScanRecord(ChkdskScanRecord record)
    {
        lock (HistoryLock)
        {
            try
            {
                var all = LoadScanHistory();
                all.Add(record);
                // Keep the file from growing unbounded - 50 entries per drive is comfortably more
                // than this card ever needs to show at once.
                var trimmed = all
                    .GroupBy(e => e.DriveLetter, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(g => g.OrderByDescending(e => e.Timestamp).Take(50))
                    .OrderBy(e => e.Timestamp)
                    .ToList();

                var dir = Path.GetDirectoryName(HistoryPath)!;
                Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(trimmed, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(HistoryPath, json);
            }
            catch
            {
                // Best-effort - if we can't persist, the app still works for this session.
            }
        }
    }

    // ================================================================================
    // #341: MSFT_Volume.Repair - Scan/SpotFix/OfflineScanAndFix, the WMI methods behind the
    // Repair-Volume cmdlet. Return-code table confirmed against Microsoft's own "Repair Method of
    // the MSFT_Volume Class" reference (root\Microsoft\Windows\Storage, Storagewmi.mof) - decoded in
    // full rather than only the 0/success case, since every value is documented.
    // ================================================================================

    public static async Task<(bool Success, uint ReturnCode, string CodeText, string ExtraDetail)> RunVolumeRepairAsync(string driveLetter, VolumeRepairMode mode)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\Storage",
                    $"SELECT * FROM MSFT_Volume WHERE DriveLetter = '{driveLetter[0]}'");
                foreach (ManagementObject vol in searcher.Get())
                {
                    var inParams = vol.GetMethodParameters("Repair");
                    inParams["OfflineScanAndFix"] = mode == VolumeRepairMode.OfflineScanAndFix;
                    inParams["Scan"] = mode == VolumeRepairMode.Scan;
                    inParams["SpotFix"] = mode == VolumeRepairMode.SpotFix;

                    var outParams = vol.InvokeMethod("Repair", inParams, null);
                    uint code = outParams?["ReturnValue"] is null ? uint.MaxValue : Convert.ToUInt32(outParams["ReturnValue"]);
                    return (code == 0, code, RepairReturnCodeText(code), BuildExtraDetail(outParams));
                }
                return (false, uint.MaxValue, "No MSFT_Volume instance found for this drive letter.", string.Empty);
            }
            catch (Exception ex)
            {
                return (false, uint.MaxValue, $"Failed: {ex.Message}", string.Empty);
            }
        });
    }

    /// <summary>Full table from Microsoft's "Repair Method of the MSFT_Volume Class" reference -
    /// every documented value decoded, not just 0/success, since every value here is genuinely
    /// documented (no guessed labels for an undocumented code).</summary>
    private static string RepairReturnCodeText(uint code) => code switch
    {
        0 => "Success.",
        1 => "Not supported.",
        2 => "Unspecified error.",
        3 => "Timeout.",
        4 => "Failed.",
        5 => "Invalid parameter.",
        7 => "Not supported on x86 running in an x64 environment.",
        40001 => "Access denied.",
        40004 => "An unexpected I/O error occurred.",
        43001 => "The specified file system is not supported.",
        43006 => "Cannot perform the requested operation when the drive is read-only.",
        43007 => "The repair failed.",
        43008 => "The scan failed.",
        43009 => "A snapshot error occurred while scanning this drive. You can try again, but if this persists, run an offline scan and fix.",
        43010 => "A scan is already running on this drive - chkdsk can't run more than one scan on a drive at a time.",
        43011 => "A snapshot error occurred while scanning this drive. You can try again, but if this persists, run an offline scan and fix.",
        43012 => "A snapshot error occurred while scanning this drive. Run an offline scan and fix.",
        43013 => "Cannot open the drive for direct access.",
        43014 => "Cannot determine the file system of the drive.",
        _ => $"Unrecognized return code {code}.",
    };

    /// <summary>The Repair method's [out] Output/ExtendedStatus parameters, best-effort flattened.
    /// Output's meaning isn't documented beyond "the output of the repair operation," so it's shown
    /// as a raw number with that caveat rather than interpreted; ExtendedStatus is documented as "a
    /// string that contains an embedded MSFT_StorageExtendedStatus object" - System.Management has
    /// been observed to surface an embedded WMI object as its own ManagementBaseObject rather than a
    /// literal string, so both shapes are handled.</summary>
    private static string BuildExtraDetail(ManagementBaseObject? outParams)
    {
        if (outParams is null) return string.Empty;
        var parts = new List<string>();

        try
        {
            if (outParams["Output"] is not null)
            {
                uint output = Convert.ToUInt32(outParams["Output"]);
                if (output != 0) parts.Add($"Operation output code: {output} (meaning not documented by Microsoft for this field).");
            }
        }
        catch { /* best-effort */ }

        try
        {
            var raw = outParams["ExtendedStatus"];
            string extended = raw switch
            {
                string s when s.Length > 0 => s,
                ManagementBaseObject embedded => FlattenEmbedded(embedded),
                _ => string.Empty,
            };
            if (extended.Length > 0) parts.Add($"Extended status: {extended}");
        }
        catch { /* best-effort */ }

        return string.Join(" ", parts);
    }

    private static string FlattenEmbedded(ManagementBaseObject embedded)
    {
        var parts = new List<string>();
        try
        {
            foreach (var prop in embedded.Properties)
            {
                if (prop.Value is null) continue;
                string text = prop.Value is Array arr ? string.Join(", ", arr.Cast<object>()) : prop.Value.ToString() ?? string.Empty;
                if (text.Length > 0) parts.Add($"{prop.Name}={text}");
            }
        }
        catch { /* best-effort */ }
        return string.Join(", ", parts);
    }

    // ================================================================================
    // #342: chkdsk result history Windows itself already logged - Wininit event 1001 at boot (the
    // same source BadSectorService.ReadLatestChkdskBadSectors already reads for just the single
    // most-recent report), plus the classic "Chkdsk" provider's online-run events. Same
    // EventLogQuery/EventLogReader shape as DiskDiagnosisEventService, degrading to empty rather
    // than throwing.
    // ================================================================================

    private const string WininitProviderName = "Microsoft-Windows-Wininit";
    private const int WininitEventId = 1001;

    // Confirmed provider name "Chkdsk" (Application log, classic non-crimson source) - 26212 read-
    // only on a volume snapshot, 26214 read/write, 26213/26226 other online-run summaries across
    // Windows versions.
    private const string ChkdskProviderName = "Chkdsk";
    private static readonly int[] ChkdskOnlineEventIds = { 26212, 26213, 26214, 26226 };

    private const int MaxHistoryEntries = 25;

    private static readonly Regex BadSectorsKbRegex = new(@"(\d[\d,]*)\s*KB in bad sectors", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ErrorsFixedLineRegex = new(@"^.*(correct|fixed|repaired).*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex VolumeOnRegex = new(@"\bon\s+([A-Za-z]):", RegexOptions.Compiled);
    private static readonly Regex VolumeParenRegex = new(@"\(([A-Za-z]):\)", RegexOptions.Compiled);

    /// <summary>Windows' own boot-time (Wininit) and online-run (Chkdsk provider) reports, most
    /// recent first.</summary>
    public static List<ChkdskHistoryEntry> ReadEventLogHistory()
    {
        var result = new List<ChkdskHistoryEntry>();
        ReadFromProvider(result, WininitProviderName, WininitEventId, "Wininit (boot-time chkdsk report)");
        foreach (int eventId in ChkdskOnlineEventIds)
            ReadFromProvider(result, ChkdskProviderName, eventId, $"Chkdsk provider (online run, event {eventId})");
        return result.OrderByDescending(e => e.Timestamp).Take(MaxHistoryEntries).ToList();
    }

    /// <summary>#342: this app's own persisted #340/#341 results plus Windows' own event-logged
    /// reports, merged into one time-ordered list - the Source column on each row states which is
    /// which rather than reconciling them into a single unlabeled feed.</summary>
    public static List<ChkdskHistoryEntry> ReadCombinedHistory()
    {
        var result = new List<ChkdskHistoryEntry>();
        result.AddRange(LoadScanHistory().Select(r => new ChkdskHistoryEntry
        {
            Timestamp = r.Timestamp,
            DriveLetter = r.DriveLetter,
            Source = $"This app ({r.Source})",
            Summary = r.Summary,
        }));
        result.AddRange(ReadEventLogHistory());
        return result.OrderByDescending(e => e.Timestamp).Take(MaxHistoryEntries).ToList();
    }

    private static void ReadFromProvider(List<ChkdskHistoryEntry> into, string providerName, int eventId, string sourceLabel)
    {
        try
        {
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='{providerName}'] and EventID={eventId}]]")
            { ReverseDirection = true };

            using var reader = new EventLogReader(query);
            int count = 0;
            while (count < MaxHistoryEntries && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    count++;
                    string message;
                    try { message = record.FormatDescription() ?? string.Empty; }
                    catch { message = string.Empty; } // provider's message file isn't registered - a known, common gap

                    string volume = ExtractVolume(message);
                    long? badSectorsKb = null;
                    var kbMatch = BadSectorsKbRegex.Match(message);
                    if (kbMatch.Success && long.TryParse(kbMatch.Groups[1].Value.Replace(",", string.Empty), out long kb))
                        badSectorsKb = kb;

                    var errorsMatch = ErrorsFixedLineRegex.Match(message);

                    into.Add(new ChkdskHistoryEntry
                    {
                        Timestamp = record.TimeCreated ?? DateTime.MinValue,
                        DriveLetter = volume,
                        Source = sourceLabel,
                        BadSectorsKb = badSectorsKb,
                        ErrorsFixedText = errorsMatch.Success ? errorsMatch.Value.Trim() : null,
                        Summary = Truncate(message, 250),
                    });
                }
            }
        }
        catch
        {
            // Provider/log unavailable, or this event simply never fired - contribute nothing.
        }
    }

    private static string ExtractVolume(string message)
    {
        var onMatch = VolumeOnRegex.Match(message);
        if (onMatch.Success) return onMatch.Groups[1].Value.ToUpperInvariant();
        var parenMatch = VolumeParenRegex.Match(message);
        return parenMatch.Success ? parenMatch.Groups[1].Value.ToUpperInvariant() : "Unknown";
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
