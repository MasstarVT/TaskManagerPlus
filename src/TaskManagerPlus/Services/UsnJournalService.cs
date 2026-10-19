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

        // FirstUsn is the oldest record still in the journal; LowestValidUsn is usually 0 and far
        // below it. Starting under FirstUsn fails the read (ERROR_JOURNAL_ENTRY_DELETED), so floor
        // at whichever of the two is higher.
        ulong lowest = Math.Max(status.FirstUsn ?? 0, status.LowestValidUsn ?? 0);
        // startUsn has to be the start of a record - an arbitrary mid-journal value fails the whole
        // read with "Error 87: The parameter is incorrect". NTFS packs records into 4 KB journal
        // pages, so a page boundary is tried first; if that read still fails without a single
        // record, it's retried from the oldest record (slower, but always a valid start).
        ulong startUsn = nextUsn > lowest + HotSpotLookbackBytes
            ? Math.Max(lowest, (nextUsn - HotSpotLookbackBytes) & ~0xFFFUL)
            : lowest;

        var cutoff = DateTime.Now.AddMinutes(-Math.Max(1, minutes));
        var read = await ReadJournalWindowAsync(driveLetter, startUsn, cutoff);
        if (read.ParsedRecords == 0 && !read.TimedOut && startUsn > lowest)
            read = await ReadJournalWindowAsync(driveLetter, lowest, cutoff);

        if (read.ParsedRecords == 0)
        {
            string detail = read.ErrorText;
            if (detail.Length > 0 || read.ExitCode is not (null or 0))
                return new UsnHotSpotResult { StatusText = detail.Length > 0 ? $"Failed: {detail}" : $"Failed: fsutil exited with code {read.ExitCode}." };
        }

        if (read.InWindow == 0 && read.UnparsedTimestamps > 0)
            return new UsnHotSpotResult { StatusText = $"Read {read.ParsedRecords:N0} USN record(s) on {driveLetter}: but couldn't interpret {read.UnparsedTimestamps:N0} of their timestamps in this system's date format, so the {minutes}-minute window couldn't be applied." };

        var top = read.ByFile.Values
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

        string note = read.TimedOut
            ? $" (stopped after {HotSpotReadTimeoutMs / 1000}s to bound the read - this reflects a partial scan, not the full window)"
            : read.Capped
                ? $" (stopped tallying after the window's first {HotSpotMaxRecordsScanned:N0} records to bound memory use - this reflects a partial scan, not the full window)"
                : string.Empty;

        return new UsnHotSpotResult
        {
            Rows = top,
            StatusText = top.Count == 0
                ? $"No USN activity found on {driveLetter}: in the last {minutes} minute(s){note}."
                : $"{read.InWindow:N0} change record(s) in the last {minutes} minute(s) - top {top.Count} file(s) by change count shown{note}.",
        };
    }

    /// <summary>Tallies from one `fsutil usn readjournal` pass.</summary>
    private sealed class HotSpotRead
    {
        public readonly Dictionary<string, HotSpotAccumulator> ByFile = new(StringComparer.OrdinalIgnoreCase);
        public int ParsedRecords;
        public int UnparsedTimestamps;
        public int InWindow;
        public bool TimedOut;
        public bool Capped;
        public int? ExitCode;
        /// <summary>stderr, else fsutil's own "Error N: ..." lines, else the last few other
        /// non-record lines - empty when nothing looked like an error.</summary>
        public string ErrorText = string.Empty;
    }

    private static async Task<HotSpotRead> ReadJournalWindowAsync(string driveLetter, ulong startUsn, DateTime cutoff)
    {
        var read = new HotSpotRead();
        var byFile = read.ByFile;
        var errorBuilder = new StringBuilder();
        // fsutil reports failures ("Error 5: Access is denied.") on stdout, not stderr - after its
        // six-line "USN Journal ID / First USN / ..." header, so error lines are kept apart from
        // the header lines rather than sharing one small cap with them.
        var errorLines = new List<string>();
        var otherLines = new Queue<string>();

        var psi = new ProcessStartInfo(ToolRunner.ResolveTool("fsutil.exe"), $"usn readjournal {driveLetter}: csv startUsn=0x{startUsn:X}")
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
            if (read.Capped) return;

            var rec = ParseCsvLine(e.Data);
            if (rec is null)
            {
                // Header row, a record shape without a file name, or an error message. Only the
                // last kind doesn't start with a USN number.
                string text = e.Data.Trim();
                if (text.Length > 0 && !char.IsDigit(text[0]) && !text.StartsWith("Usn,", StringComparison.OrdinalIgnoreCase))
                {
                    if (text.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                    {
                        if (errorLines.Count < 3) errorLines.Add(text);
                    }
                    else
                    {
                        otherLines.Enqueue(text);
                        if (otherLines.Count > 3) otherLines.Dequeue();
                    }
                }
                return;
            }
            read.ParsedRecords++;

            // A record whose time can't be read can't be placed in the window - counted, not
            // silently treated as recent.
            if (rec.Value.Timestamp is not { } ts) { read.UnparsedTimestamps++; return; }

            // The journal reads oldest-first, so the in-window records are the last ones out;
            // only those count against the cap, or a long pre-window run would use it all up.
            if (ts < cutoff) return; // outside the requested window
            if (read.InWindow >= HotSpotMaxRecordsScanned) { read.Capped = true; return; }
            read.InWindow++;

            if (!byFile.TryGetValue(rec.Value.FileName, out var acc))
                byFile[rec.Value.FileName] = acc = new HotSpotAccumulator { FileName = rec.Value.FileName };
            acc.ParentFrn = rec.Value.ParentFileId;
            acc.Count++;
            acc.Apply(rec.Value.Reason);
        }

        proc.OutputDataReceived += HandleOutput;
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) errorBuilder.AppendLine(e.Data); };

        if (!proc.Start())
        {
            read.ErrorText = "Couldn't start fsutil.exe.";
            return read;
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(HotSpotReadTimeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            read.TimedOut = true;
        }
        finally
        {
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }
        }

        // #1044: after a timeout Kill, the async OutputDataReceived callbacks are still draining
        // already-buffered lines - and mutating byFile - so enumerating it afterwards would race
        // them (InvalidOperationException, or silently corrupted tallies) on exactly the
        // large-journal busy-volume case the timeout exists for. A second, short-bounded
        // WaitForExitAsync on the now-killed process also awaits the redirected streams' EOF, i.e.
        // every pending callback has been raised by the time it completes; if even that doesn't
        // finish, cancel the async reads outright as a last resort before touching byFile.
        try
        {
            using var drainCts = new CancellationTokenSource(5000);
            await proc.WaitForExitAsync(drainCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.CancelOutputRead(); } catch { /* best-effort */ }
            try { proc.CancelErrorRead(); } catch { /* best-effort */ }
        }

        if (!read.TimedOut) { try { read.ExitCode = proc.ExitCode; } catch { /* leave null */ } }

        string detail = errorBuilder.ToString().Trim();
        if (detail.Length == 0) detail = string.Join(" ", errorLines);
        if (detail.Length == 0 && read.ExitCode is not (null or 0)) detail = string.Join(" ", otherLines);
        read.ErrorText = detail;
        return read;
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

    /// <summary>Column order is fsutil's own csv header (its row format string is
    /// `%I64d,"%.*ws",%d,0x%08x,"%s","%s",0x%08x,"%s",%s,%s,...`): Usn, File name, File name
    /// length, Reason #, Reason, Time stamp, File attributes #, File attributes, File ID, Parent
    /// file ID, Source info #, Source info, Security ID, Major version, Minor version, Record
    /// length. A header row (or any line whose first field isn't a parseable USN) is skipped rather
    /// than mis-recorded as a real change.</summary>
    private static UsnCsvRecord? ParseCsvLine(string line)
    {
        var fields = SplitCsvLine(line);
        if (fields.Count < 10) return null;
        if (!TryParseHexOrDecimal(fields[0], out _)) return null;

        string fileName = fields[1].Trim();
        if (fileName.Length == 0) return null;

        uint reason = TryParseHexOrDecimal(fields[3], out ulong reasonRaw) ? (uint)reasonRaw : 0;
        // #1058: fsutil renders the timestamp column in the machine's own locale format (like
        // vssadmin's creation times - see VssService), so CurrentCulture is correct here, unlike
        // the invariant hex/decimal fields above. Parsing it invariant returned null on any
        // non-US locale, and a null timestamp bypasses FindHotSpotsAsync's window filter - every
        // record then counted as "in the last N minutes" while StatusText claimed a windowed count.
        DateTime? timestamp = DateTime.TryParse(fields[5].Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out var dt) ? dt : null;
        ulong parentFrn = TryParseFileId(fields[9], out ulong pf) ? pf : 0;

        return new UsnCsvRecord(fileName, reason, timestamp, parentFrn);
    }

    /// <summary>File IDs are printed as hex and can be 128-bit (USN_RECORD_V3); on NTFS the upper
    /// half is zero, so the low 64 bits are the file reference number.</summary>
    private static bool TryParseFileId(string field, out ulong value)
    {
        string s = field.Trim().Trim('"');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length > 16) s = s[^16..];
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
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
            var psi = new ProcessStartInfo(ToolRunner.ResolveTool(exe), args)
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
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
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
