using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 16, #346/#347/#348: USN (Update Sequence Number) change-journal status, churn hot-spot
/// aggregation, and create/resize/delete controls - all `fsutil usn ...` shell-outs. Follows the same
/// RunToolAsync shape NtfsFilesystemService/VolumeDiagnosticsService already use (concurrent stdout/
/// stderr reads + a bounded wait + Kill()-on-timeout) - duplicated here rather than shared, same call
/// NtfsFilesystemService's own remarks already make for this app's small, self-contained shell-out
/// helpers.
/// </summary>
public static class UsnJournalService
{
    // ================================================================================
    // #346: journal status.
    // ================================================================================

    // Confirmed wording against a live `fsutil usn queryjournal C:` run while building this:
    //   Usn Journal ID   : 0x01dd1ce5e2f1a528
    //   First Usn        : 0x000000009d800000
    //   Next Usn         : 0x000000009fc7b198
    //   Lowest Valid Usn : 0x0000000000000000
    //   Max Usn          : 0x00000fffffff0000
    //   Maximum Size     : 0x0000000002000000 (32.0 MB)
    //   Allocation Delta : 0x0000000000800000 ( 8.0 MB)
    //   Minimum record version supported : 2
    //   Maximum record version supported : 4
    //   Write range tracking: Disabled
    // Parsed as generic "Label : 0xHEX" lines (case/spacing-tolerant) rather than a strict per-field
    // match, so an older/newer Windows build that adds, drops, or reorders lines (the version-
    // supported/write-range-tracking lines above aren't documented as stable across releases)
    // degrades gracefully instead of failing the whole read.
    private static readonly Regex JournalHexLineRegex = new(
        @"^(?<key>[A-Za-z][A-Za-z0-9 ]*?)\s*:\s*(?<val>0[xX][0-9A-Fa-f]+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>`fsutil usn queryjournal &lt;vol&gt;` - Available=false for the common "no journal
    /// exists yet" case (non-zero exit, or the journal-ID line simply isn't present) as well as any
    /// real failure; never fabricates a status.</summary>
    public static async Task<UsnJournalStatus> QueryStatusAsync(string driveLetter)
    {
        var (exitCode, output) = await RunToolAsync("fsutil.exe", $"usn queryjournal {driveLetter}:", 8000);
        string trimmed = output.Trim();
        if (exitCode != 0)
        {
            return new UsnJournalStatus
            {
                Available = false,
                UnavailableReason = trimmed.Length > 0 ? trimmed : $"fsutil exited with code {exitCode}.",
                RawText = trimmed,
            };
        }

        var values = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in JournalHexLineRegex.Matches(trimmed))
        {
            string key = Regex.Replace(m.Groups["key"].Value.Trim(), @"\s+", " ");
            if (ulong.TryParse(m.Groups["val"].Value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong v))
                values.TryAdd(key, v);
        }

        ulong? Get(params string[] keys)
        {
            foreach (var k in keys)
                if (values.TryGetValue(k, out var v)) return v;
            return null;
        }

        var journalId = Get("Usn Journal ID");
        if (journalId is null)
        {
            // No recognizable journal-ID line - either a build with different wording, or (far more
            // commonly) the journal simply isn't active on this volume.
            return new UsnJournalStatus
            {
                Available = false,
                UnavailableReason = trimmed.Length > 0 ? trimmed : "No active USN journal on this volume.",
                RawText = trimmed,
            };
        }

        return new UsnJournalStatus
        {
            Available = true,
            JournalId = journalId,
            FirstUsn = Get("First Usn"),
            NextUsn = Get("Next Usn"),
            LowestValidUsn = Get("Lowest Valid Usn"),
            MaxUsn = Get("Max Usn"),
            MaximumSizeBytes = Get("Maximum Size"),
            AllocationDeltaBytes = Get("Allocation Delta"),
            RawText = trimmed,
        };
    }

    // ================================================================================
    // #348: create/resize/delete controls.
    // ================================================================================

    /// <summary>`fsutil usn createjournal &lt;vol&gt; [m=&lt;bytes&gt;] [a=&lt;bytes&gt;]` -
    /// per fsutil's own usage text this also resizes an already-existing journal (it doesn't error
    /// out if one is already active), so one method covers both "create" and "resize". Either size
    /// left null omits that option, letting fsutil pick its own volume-size-based default.</summary>
    public static async Task<(bool Success, string Message)> CreateOrResizeJournalAsync(string driveLetter, long? maxSizeBytes, long? allocationDeltaBytes)
    {
        string args = $"usn createjournal {driveLetter}:";
        if (maxSizeBytes is { } m) args += $" m={m}";
        if (allocationDeltaBytes is { } a) args += $" a={a}";

        var (exitCode, output) = await RunToolAsync("fsutil.exe", args, 15000);
        string trimmed = output.Trim();
        if (exitCode == 0) return (true, trimmed.Length > 0 ? trimmed : "USN journal created/resized.");
        return (false, trimmed.Length > 0 ? trimmed : $"fsutil exited with code {exitCode}.");
    }

    /// <summary>`fsutil usn deletejournal /D &lt;vol&gt;` - a generous timeout since deleting a large
    /// journal on a busy volume involves real bookkeeping work, not an instant call.</summary>
    public static async Task<(bool Success, string Message)> DeleteJournalAsync(string driveLetter)
    {
        var (exitCode, output) = await RunToolAsync("fsutil.exe", $"usn deletejournal /D {driveLetter}:", 30000);
        string trimmed = output.Trim();
        if (exitCode == 0) return (true, trimmed.Length > 0 ? trimmed : "USN journal deleted.");
        return (false, trimmed.Length > 0 ? trimmed : $"fsutil exited with code {exitCode}.");
    }

    // ================================================================================
    // #347: USN churn hot spots - "what is writing to this disk" for the last N minutes.
    // ================================================================================

    // No pagelength/record-count option exists on `fsutil usn readjournal` (confirmed against this
    // build's own usage text: minVer/maxVer/startUsn/csv/wait/tail is the complete option list), so
    // this read is self-bounded entirely on this side - a startUsn estimate to avoid starting from
    // the very beginning of a possibly-ancient journal, plus a hard wall-clock timeout and a hard
    // record-count cap enforced while streaming, matching CLAUDE.md's "cap how much you parse/
    // display, don't materialize an unbounded list" guidance for this exact item.
    private const int HotSpotMaxRecordsScanned = 20_000;
    private const int HotSpotReadTimeoutMs = 45_000;
    private const int HotSpotTopN = 20;

    // ~300 MB of raw USN record data is comfortably enough to cover several minutes of even a very
    // busy volume's churn (typical records run well under 200 bytes each), without risking reading
    // from the true start of a multi-GB journal on an old, heavily-used volume.
    private const ulong HotSpotLookbackBytes = 300UL * 1024 * 1024;

    public static async Task<UsnHotSpotResult> FindHotSpotsAsync(string driveLetter, int minutes)
    {
        var status = await QueryStatusAsync(driveLetter);
        if (!status.Available || status.NextUsn is not { } nextUsn)
            return new UsnHotSpotResult { StatusText = $"USN journal unavailable on {driveLetter}: - {status.UnavailableReason}" };

        ulong lowest = status.LowestValidUsn ?? status.FirstUsn ?? 0;
        ulong startUsn = nextUsn > lowest + HotSpotLookbackBytes ? nextUsn - HotSpotLookbackBytes : lowest;

        var cutoff = DateTime.Now.AddMinutes(-Math.Max(1, minutes));
        var byFile = new Dictionary<string, HotSpotAccumulator>(StringComparer.OrdinalIgnoreCase);
        int totalRecords = 0;
        int inWindow = 0;
        bool timedOut = false;
        bool capped = false;
        var errorBuilder = new StringBuilder();

        var psi = new ProcessStartInfo("fsutil.exe", $"usn readjournal {driveLetter}: csv startUsn=0x{startUsn:X}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        void HandleOutput(object? sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            totalRecords++;
            if (totalRecords > HotSpotMaxRecordsScanned) { capped = true; return; }

            var rec = ParseCsvLine(e.Data);
            if (rec is null) return; // header row, or a line this parse couldn't make sense of

            if (rec.Value.Timestamp is { } ts && ts < cutoff) return; // outside the requested window
            inWindow++;

            if (!byFile.TryGetValue(rec.Value.FileName, out var acc))
                byFile[rec.Value.FileName] = acc = new HotSpotAccumulator { FileName = rec.Value.FileName };
            acc.ParentFrn = rec.Value.ParentFileId;
            acc.Count++;
            acc.Apply(rec.Value.Reason);
        }

        proc.OutputDataReceived += HandleOutput;
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) errorBuilder.AppendLine(e.Data); };

        if (!proc.Start())
            return new UsnHotSpotResult { StatusText = "Couldn't start fsutil.exe." };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(HotSpotReadTimeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
        }
        finally
        {
            if (!proc.HasExited)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
            }
        }

        if (totalRecords == 0 && errorBuilder.Length > 0)
            return new UsnHotSpotResult { StatusText = $"Failed: {errorBuilder.ToString().Trim()}" };

        var top = byFile.Values
            .OrderByDescending(a => a.Count)
            .Take(HotSpotTopN)
            .Select(a => new UsnHotSpotRow
            {
                FileName = a.FileName,
                ChangeCount = a.Count,
                ParentFrnText = $"Folder ref 0x{a.ParentFrn:X16}",
                ReasonBreakdownText = a.BreakdownText(),
            })
            .ToList();

        string note = timedOut
            ? $" (stopped after {HotSpotReadTimeoutMs / 1000}s to bound the read - this reflects a partial scan, not the full window)"
            : capped
                ? $" (stopped after {HotSpotMaxRecordsScanned:N0} records to bound memory use - this reflects a partial scan, not the full window)"
                : string.Empty;

        return new UsnHotSpotResult
        {
            Rows = top,
            StatusText = top.Count == 0
                ? $"No USN activity found on {driveLetter}: in the last {minutes} minute(s){note}."
                : $"{inWindow:N0} change record(s) in the last {minutes} minute(s) - top {top.Count} file(s) by change count shown{note}.",
        };
    }

    /// <summary>One row's running tally across a #347 read. FileName is the changed file's own name
    /// (present directly on every USN_RECORD - no extra lookup needed); ParentFrn is kept as a raw
    /// hex Parent File Reference Number rather than resolved to a folder path, since that resolution
    /// needs an OpenFileById-style lookup with no existing Windows-tool equivalent (see
    /// UsnHotSpotRow's remarks) - out of scope for this pass.</summary>
    private sealed class HotSpotAccumulator
    {
        public string FileName = string.Empty;
        public ulong ParentFrn;
        public int Count;
        private int _dataOverwrite, _dataExtend, _fileCreate, _fileDelete, _rename, _securityChange, _other;

        // Public, documented USN_REASON_* bit values from winioctl.h - decoded directly from the
        // Reason column's own hex value fsutil already gives us, not a fresh IOCTL/interop call.
        private const uint ReasonDataOverwrite = 0x00000001;
        private const uint ReasonDataExtend = 0x00000002;
        private const uint ReasonFileCreate = 0x00000100;
        private const uint ReasonFileDelete = 0x00000200;
        private const uint ReasonSecurityChange = 0x00000800;
        private const uint ReasonRenameOldName = 0x00001000;
        private const uint ReasonRenameNewName = 0x00002000;

        public void Apply(uint reason)
        {
            bool matched = false;
            if ((reason & ReasonDataOverwrite) != 0) { _dataOverwrite++; matched = true; }
            if ((reason & ReasonDataExtend) != 0) { _dataExtend++; matched = true; }
            if ((reason & ReasonFileCreate) != 0) { _fileCreate++; matched = true; }
            if ((reason & ReasonFileDelete) != 0) { _fileDelete++; matched = true; }
            if ((reason & ReasonSecurityChange) != 0) { _securityChange++; matched = true; }
            if ((reason & (ReasonRenameOldName | ReasonRenameNewName)) != 0) { _rename++; matched = true; }
            if (!matched) _other++;
        }

        public string BreakdownText()
        {
            var parts = new List<string>();
            if (_dataOverwrite > 0) parts.Add($"Overwrite ×{_dataOverwrite}");
            if (_dataExtend > 0) parts.Add($"Extend ×{_dataExtend}");
            if (_fileCreate > 0) parts.Add($"Create ×{_fileCreate}");
            if (_fileDelete > 0) parts.Add($"Delete ×{_fileDelete}");
            if (_rename > 0) parts.Add($"Rename ×{_rename}");
            if (_securityChange > 0) parts.Add($"Security ×{_securityChange}");
            if (_other > 0) parts.Add($"Other ×{_other}");
            return string.Join(", ", parts);
        }
    }

    private readonly record struct UsnCsvRecord(string FileName, uint Reason, DateTime? Timestamp, ulong ParentFileId);

    /// <summary>Column order follows the documented USN_RECORD field order `fsutil usn readjournal
    /// ... csv` mirrors: USN, File name, Reason, Time stamp, File attributes, File ID, Parent File
    /// ID, Security ID, Major version, Minor version, Record length. A header row (or any line whose
    /// first field isn't a parseable USN) is skipped rather than mis-recorded as a real change.</summary>
    private static UsnCsvRecord? ParseCsvLine(string line)
    {
        var fields = SplitCsvLine(line);
        if (fields.Count < 7) return null;
        if (!TryParseHexOrDecimal(fields[0], out _)) return null;

        string fileName = fields[1].Trim();
        if (fileName.Length == 0) return null;

        uint reason = TryParseHexOrDecimal(fields[2], out ulong reasonRaw) ? (uint)reasonRaw : 0;
        DateTime? timestamp = DateTime.TryParse(fields[3].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
        ulong parentFrn = TryParseHexOrDecimal(fields[6], out ulong pf) ? pf : 0;

        return new UsnCsvRecord(fileName, reason, timestamp, parentFrn);
    }

    private static bool TryParseHexOrDecimal(string field, out ulong value)
    {
        string s = field.Trim().Trim('"');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Minimal quoted-field CSV splitter - fsutil quotes a filename field only when it needs
    /// to (e.g. an embedded comma), so a naive Split(',') would misalign columns for those rows.</summary>
    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ',' && !inQuotes) { result.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    // ================================================================================
    // Shared shell-out helper - same shape as NtfsFilesystemService.RunToolAsync (see its remarks).
    // ================================================================================

    private static async Task<(int ExitCode, string Output)> RunToolAsync(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (-1, $"Couldn't start {exe}.");

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
