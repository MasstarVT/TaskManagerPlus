using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #462/#463: parses %windir%\INF\setupapi.dev.log for a dated timeline of every device install/
/// update/configure Windows Setup has logged, plus the subset that failed. This file can run tens
/// of MB on a machine with a long uptime/many installs - per CLAUDE.md's on-demand convention and
/// the suggestion text itself, callers must gate this behind an explicit button, never load it
/// automatically. A single forward StreamReader pass (never loading the whole file into memory at
/// once) is enough here - no seeking/backwards reads needed.
///
/// setupapi.dev.log's own format (unchanged across Windows releases, though not a Microsoft-
/// published versioned schema) delimits one "section" per install/update/configure attempt:
///   &gt;&gt;&gt;  [Device Install (Hardware initiated) - USB\VID_1234&amp;PID_5678\...]
///   &gt;&gt;&gt;  Section start 2024/03/01 10:15:23.456
///        ... body lines, "!!!" prefix marks an error line ...
///   &lt;&lt;&lt;  Section end 2024/03/01 10:15:25.789
///   &lt;&lt;&lt;  [Exit status: SUCCESS]
/// A section is treated as a failure when it logged at least one "!!!" error line, or its exit
/// status isn't "SUCCESS".
/// </summary>
public static class DriverInstallLogService
{
    public static readonly string DefaultLogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "setupapi.dev.log");

    private const int MaxEntriesReturned = 3000;

    public sealed record ParseResult(List<DriverInstallEvent> Timeline, List<DriverInstallFailure> Failures, string? ErrorMessage);

    private static readonly Regex HeaderRegex = new(@"^>>>\s*\[(.+)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex SectionStartRegex = new(@"^>>>\s*Section start\s+(.+?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExitStatusRegex = new(@"^<<<\s*\[Exit status:\s*(.+?)\]\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HexCodeRegex = new(@"0x[0-9A-Fa-f]{6,8}", RegexOptions.Compiled);

    /// <summary>filterSince null means "everything the log has" (the #462 "last 30 days" filter
    /// toggled off) - a section whose own timestamp couldn't be parsed is still included either way
    /// rather than silently dropped.</summary>
    public static Task<ParseResult> ParseAsync(DateTime? filterSince) => Task.Run(() => Parse(filterSince));

    private static ParseResult Parse(DateTime? filterSince)
    {
        var timeline = new List<DriverInstallEvent>();
        var failures = new List<DriverInstallFailure>();

        if (!File.Exists(DefaultLogPath))
            return new ParseResult(timeline, failures,
                "setupapi.dev.log wasn't found at " + DefaultLogPath + " - nothing has been logged yet, or it lives elsewhere on this system.");

        try
        {
            using var stream = new FileStream(DefaultLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            bool inSection = false;
            string? headerText = null;
            DateTime? sectionStart = null;
            var errorLines = new List<string>();

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.StartsWith(">>>", StringComparison.Ordinal))
                {
                    var headerMatch = HeaderRegex.Match(line);
                    if (headerMatch.Success)
                    {
                        headerText = headerMatch.Groups[1].Value;
                        inSection = true;
                        sectionStart = null;
                        errorLines = new List<string>();
                        continue;
                    }

                    if (inSection)
                    {
                        var startMatch = SectionStartRegex.Match(line);
                        if (startMatch.Success)
                        {
                            sectionStart = TryParseTimestamp(startMatch.Groups[1].Value);
                            continue;
                        }
                    }
                }
                else if (inSection && line.StartsWith("!!!", StringComparison.Ordinal))
                {
                    string text = line.TrimStart('!', ' ');
                    if (text.Length > 0 && errorLines.Count < 20) errorLines.Add(text);
                }
                else if (inSection && line.StartsWith("<<<", StringComparison.Ordinal))
                {
                    var exitMatch = ExitStatusRegex.Match(line);
                    if (exitMatch.Success)
                    {
                        string exitStatus = exitMatch.Groups[1].Value.Trim();
                        EmitSection(timeline, failures, headerText, sectionStart, exitStatus, errorLines, filterSince);

                        inSection = false;
                        headerText = null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return new ParseResult(timeline, failures, $"Couldn't read setupapi.dev.log: {ex.Message}");
        }

        timeline = timeline.OrderByDescending(e => e.Timestamp).Take(MaxEntriesReturned).ToList();
        failures = failures.OrderByDescending(e => e.Timestamp).Take(MaxEntriesReturned).ToList();
        return new ParseResult(timeline, failures, null);
    }

    private static void EmitSection(List<DriverInstallEvent> timeline, List<DriverInstallFailure> failures,
        string? headerText, DateTime? sectionStart, string exitStatus, List<string> errorLines, DateTime? filterSince)
    {
        if (headerText is null) return;

        var time = sectionStart ?? DateTime.MinValue;
        if (filterSince is { } since && time != DateTime.MinValue && time < since) return;

        var (category, target) = SplitHeader(headerText);
        bool isFailure = errorLines.Count > 0 ||
            (exitStatus.Length > 0 && !exitStatus.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase));

        timeline.Add(new DriverInstallEvent
        {
            Timestamp = time,
            Category = category,
            Target = target,
            ExitStatus = exitStatus,
            IsFailure = isFailure,
        });

        if (isFailure)
        {
            string errorText = errorLines.Count > 0 ? string.Join(" | ", errorLines) : exitStatus;
            var codeMatch = HexCodeRegex.Match(errorText);
            failures.Add(new DriverInstallFailure
            {
                Timestamp = time,
                Category = category,
                Target = target,
                ErrorCode = codeMatch.Success ? codeMatch.Value : null,
                ErrorText = Truncate(errorText, 400),
            });
        }
    }

    /// <summary>Header text is "Category - Target" (e.g. "Device Install (Hardware initiated) -
    /// USB\VID_1234&amp;PID_5678\...") when a target is present, or just "Category" for a handful of
    /// section kinds that don't apply to one specific device (e.g. "Import Driver Package").</summary>
    private static (string Category, string Target) SplitHeader(string headerText)
    {
        int idx = headerText.IndexOf(" - ", StringComparison.Ordinal);
        return idx < 0 ? (headerText, string.Empty) : (headerText[..idx], headerText[(idx + 3)..]);
    }

    private static readonly string[] TimestampFormats =
    {
        "yyyy/MM/dd HH:mm:ss.fff", "MM/dd/yyyy HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss.fff",
    };

    private static DateTime? TryParseTimestamp(string text)
    {
        foreach (var format in TimestampFormats)
        {
            if (DateTime.TryParseExact(text, format, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var exact))
                return exact;
        }
        return DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "…";
}
