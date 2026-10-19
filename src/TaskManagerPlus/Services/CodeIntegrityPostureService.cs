using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>
/// #456: code-integrity bypass detection for the Devices &amp; Drivers tab's security-posture row.
/// Two independent signals, both read fresh on every Refresh (this tab is already on-demand, so
/// there's no timer to worry about re-triggering these):
///   1. `bcdedit /enum {current}` - parsed the same way ScheduledTaskService/VolumeDiagnosticsService
///      shell out to and parse a known Windows tool's text output, for testsigning/nointegritychecks/
///      a loadoptions DDISABLE_INTEGRITY_CHECKS entry - the boot-configuration settings that relax
///      Driver Signature Enforcement.
///   2. Microsoft-Windows-CodeIntegrity/Operational, events 3004 (an image failed a signing
///      requirement) and 3033 (Code Integrity found a hash mismatch / policy violation) over the
///      same 30-day lookback window EventLogService's own queries use.
/// Neither throws: a bcdedit failure reports its flags as Unknown (null), and an event-log failure
/// (locked-down policy, log not present on this Windows edition/SKU, ...) reports the event count as
/// Unknown (null) - never as a clean 0.
/// </summary>
public static class CodeIntegrityPostureService
{
    /// <summary>The three bcdedit flags are null (Unknown) when bcdedit failed or timed out, or
    /// printed a value other than Yes/No/On/Off (its values are localized - "Ja"/"Nein" on a German
    /// install) - never reported as a clean "off". BlockedImageEventCount is null when the
    /// CodeIntegrity log couldn't be read; BlockedImageEventCountCapped means the read stopped at
    /// the cap, so the real count is higher.</summary>
    public sealed record CodeIntegrityPosture(
        bool? TestSigningEnabled,
        bool? NoIntegrityChecksEnabled,
        bool? DriverSignatureEnforcementDisabled,
        int? BlockedImageEventCount,
        bool BlockedImageEventCountCapped,
        DateTime? LastBlockedImageEventTime,
        IReadOnlyList<string> RecentBlockedImageMessages);

    private const int LookbackDays = 30;

    // "Code Integrity determined that a process (X) attempted to load Y that did not meet the
    // Windows signing level requirements" and the corresponding hash/policy-violation event.
    private const int SigningRequirementFailedEventId = 3004;
    private const int HashMismatchEventId = 3033;

    public static async Task<CodeIntegrityPosture> ReadAsync()
    {
        var (testSigning, noIntegrityChecks, dseDisabled) = await ReadBcdOptionsAsync();
        // Up to 201 records plus FormatDescription - kept off the caller's (UI) thread.
        var (count, capped, last, messages) = await Task.Run(ReadBlockedImageEvents);
        return new CodeIntegrityPosture(testSigning, noIntegrityChecks, dseDisabled, count, capped, last, messages);
    }

    private static async Task<(bool? TestSigning, bool? NoIntegrityChecks, bool? DseDisabled)> ReadBcdOptionsAsync()
    {
        try
        {
            var (output, exitCode) = await RunCapturedAsync("bcdedit.exe", "/enum {current}");
            var fields = ParseFields(output);
            // Timed out, failed (e.g. not elevated) or printed nothing parseable - Unknown.
            if (exitCode != 0 || fields.Count == 0) return (null, null, null);

            // bcdedit omits an element that isn't set, so an absent field is a real "off".
            bool? testSigning = ReadFlag(fields, "testsigning");
            bool? noIntegrityChecks = ReadFlag(fields, "nointegritychecks");
            // The loadoptions flag has appeared with both one and two leading D's in different
            // Windows releases' own documentation - match the substring loosely rather than an
            // exact token.
            bool loadOptionsDisable = fields.TryGetValue("loadoptions", out var lo) &&
                lo.Contains("DISABLE_INTEGRITY_CHECKS", StringComparison.OrdinalIgnoreCase);

            bool? dseDisabled = (loadOptionsDisable || noIntegrityChecks == true) ? true : noIntegrityChecks;
            return (testSigning, noIntegrityChecks, dseDisabled);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static bool? ReadFlag(Dictionary<string, string> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value)) return false;
        string v = value.Trim();
        if (v.Equals("Yes", StringComparison.OrdinalIgnoreCase) || v.Equals("On", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Equals("No", StringComparison.OrdinalIgnoreCase) || v.Equals("Off", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    /// <summary>bcdedit's `/enum` output is column-aligned plain text, one "name  value" pair per
    /// line (name and value separated by two or more spaces) - not a structured format, but a
    /// stable one bcdedit has used unchanged across every supported Windows release.</summary>
    private static Dictionary<string, string> ParseFields(string output)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            // Values start at column 24, so a 23+-character name is followed by a single space.
            var match = Regex.Match(line, @"^(?:(?<key>[A-Za-z][A-Za-z0-9]*)\s{2,}|(?<key>[A-Za-z][A-Za-z0-9]{22,}) )(?<value>.+)$");
            if (!match.Success) continue;
            fields[match.Groups["key"].Value] = match.Groups["value"].Value.Trim();
        }
        return fields;
    }

    private const int MaxBlockedImageEvents = 200;

    private static (int? Count, bool Capped, DateTime? Last, List<string> Messages) ReadBlockedImageEvents()
    {
        var messages = new List<string>();
        int count = 0;
        bool capped = false;
        DateTime? last = null;
        try
        {
            long maxAgeMs = LookbackDays * 24L * 60 * 60 * 1000;
            var query = new EventLogQuery("Microsoft-Windows-CodeIntegrity/Operational", PathType.LogName,
                $"*[System[(EventID={SigningRequirementFailedEventId} or EventID={HashMismatchEventId}) and " +
                $"TimeCreated[timediff(@SystemTime) <= {maxAgeMs}]]]")
            {
                ReverseDirection = true,
            };

            using var reader = new EventLogReader(query);
            while (reader.ReadEvent() is { } record)
            {
                // One record past the cap only proves there are more - don't count or format it.
                if (count == MaxBlockedImageEvents)
                {
                    record.Dispose();
                    capped = true;
                    break;
                }
                using (record)
                {
                    count++;
                    last ??= record.TimeCreated;
                    if (messages.Count < 10)
                    {
                        string msg;
                        try { msg = record.FormatDescription() ?? $"Event {record.Id}"; }
                        catch { msg = $"Event {record.Id}"; }
                        if (string.IsNullOrWhiteSpace(msg)) msg = $"Event {record.Id}";
                        if (msg.Length > 220) msg = msg[..220] + "...";
                        messages.Add(msg);
                    }
                }
            }
        }
        catch
        {
            // Log unavailable on this Windows edition, access denied, or provider not registered -
            // Unknown, not "nothing found".
            return (null, false, null, new List<string>());
        }
        return (count, capped, last, messages);
    }

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's default timeout (empty output and a null exit
    /// code for a timed-out run).</summary>
    private static Task<(string Output, int? ExitCode)> RunCapturedAsync(string exe, string args, int timeoutMs = 10000)
        => ToolRunner.RunCapturedAsync(exe, args, timeoutMs, timeoutOutput: string.Empty);
}
