using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #230/#232: two more `powercfg`-text-parse features that don't fit
/// PowerSchemeInterruptSteeringService's narrow "Interrupt Steering" scope, so they get their own
/// small service rather than growing that one file into a grab-bag - same "known tool, text output
/// parsed" tradeoff.
///
/// #230: `powercfg /requests` - the tool's own text output already names the process/service/
/// driver holding each outstanding power request per section, one per line; this just splits it
/// into rows. A request held forever (especially EXECUTION/PERFBOOST) changes scheduling/idle
/// behavior and is otherwise completely invisible.
///
/// #232: `powercfg -q` (the active scheme's full settings dump) for a handful of latency-relevant
/// processor settings - reuses the same "split into (GUID, friendly-name, body) blocks, then
/// search case-insensitively by setting-name text" approach PowerSchemeInterruptSteeringService's
/// #220 already established (kept as a separate private copy here rather than shared - matching
/// this codebase's existing per-service RunCapturedAsync duplication convention), since
/// `powercfg -q`'s setting names aren't a documented, versioned contract the way a registry value
/// name is, and a name-text match is more stable across Windows builds than a hardcoded GUID.
/// Per-setting "balanced default" notes are only given where Microsoft's own stock Balanced-scheme
/// default is well-known and stable across builds; settings without a reliably documented
/// cross-build default get a neutral "compare against a stock Balanced scheme" note instead of a
/// guessed number, per this app's "never fabricate" rule.
/// </summary>
public static class LatencyPowerSettingsService
{
    private static readonly string[] RequestSections = { "DISPLAY", "SYSTEM", "AWAYMODE", "EXECUTION", "PERFBOOST" };

    /// <summary>#230.</summary>
    public static async Task<List<PowerRequestRow>> ReadPowerRequestsAsync()
    {
        var rows = new List<PowerRequestRow>();
        try
        {
            string output = await RunCapturedAsync("powercfg.exe", "/requests");
            if (string.IsNullOrWhiteSpace(output)) return rows;

            foreach (var section in RequestSections)
            {
                var m = Regex.Match(output, $@"^{section}:\s*\r?\n(.*?)(?=^[A-Z]+:\s*\r?\n|\z)", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (!m.Success) continue;

                string body = m.Groups[1].Value.Trim();
                if (body.Length == 0 || body.Equals("None.", StringComparison.OrdinalIgnoreCase)) continue;

                string? holder = null;
                var reasonLines = new List<string>();
                void Flush()
                {
                    if (holder is null) return;
                    rows.Add(new PowerRequestRow
                    {
                        Type = section,
                        Holder = reasonLines.Count > 0 ? $"{holder} — {string.Join(" ", reasonLines)}" : holder,
                    });
                }

                foreach (var rawLine in body.Replace("\r\n", "\n").Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith('['))
                    {
                        Flush();
                        holder = line;
                        reasonLines = new List<string>();
                    }
                    else if (holder is not null)
                    {
                        reasonLines.Add(line);
                    }
                }
                Flush();
            }
        }
        catch
        {
            // best-effort - an empty list just means the card shows "no outstanding requests found".
        }
        return rows;
    }

    // #232: the settings this reads, with a name-hint list (primary match), an optional GUID-prefix
    // fallback (only used for the one setting the task itself supplied a candidate GUID for), and a
    // "balanced default" note.
    private static readonly (string DisplayName, string[] Hints, string? GuidPrefix, string? BalancedDefaultNote)[] Settings =
    {
        ("Minimum processor state", new[] { "minimum processor state" }, null,
            "Balanced-scheme default (as shipped by Windows) is typically 5% AC / 5% DC — some OEM images change this."),
        ("Processor idle disable", new[] { "idle disable" }, null,
            "Default is Off (idle states enabled) — On disables CPU idle states entirely and increases power draw. This setting is hidden in many Windows builds/schemes, so not finding it is expected."),
        ("Processor idle promote threshold", new[] { "idle promote threshold" }, null,
            "No single documented cross-build default — compare against a stock Balanced scheme if this looks unusually high or low."),
        ("Processor idle demote threshold", new[] { "idle demote threshold" }, null,
            "No single documented cross-build default — compare against a stock Balanced scheme if this looks unusually high or low."),
        ("Processor performance increase policy", new[] { "performance increase policy" }, null,
            "Balanced-scheme default is typically \"Ideal\" (a gradual ramp) rather than an aggressive policy."),
        ("Processor performance increase threshold", new[] { "performance increase threshold" }, null,
            "No single documented cross-build default — compare against a stock Balanced scheme if this looks unusually high or low."),
        ("Latency sensitivity hint processor performance", new[] { "latency sensitivity" }, "619b7505",
            "Not present in every Windows build/scheme. When present, a high value tells the processor driver to prioritize latency over energy savings for hinted workloads."),
    };

    /// <summary>#232.</summary>
    public static async Task<List<PlatformLatencySettingRow>> ReadLatencySensitiveSettingsAsync()
    {
        try
        {
            string output = await RunCapturedAsync("powercfg.exe", "-q");
            if (string.IsNullOrWhiteSpace(output))
                return Settings.Select(s => NotFoundRow(s.DisplayName, "powercfg -q produced no output.")).ToList();

            var blocks = SplitIntoBlocks(output);
            var rows = new List<PlatformLatencySettingRow>();
            foreach (var s in Settings)
            {
                var block = blocks.FirstOrDefault(b => s.Hints.Any(h => b.Header.Contains(h, StringComparison.OrdinalIgnoreCase)));
                if (block.Header is null && s.GuidPrefix is not null)
                    block = blocks.FirstOrDefault(b => b.Guid.StartsWith(s.GuidPrefix, StringComparison.OrdinalIgnoreCase));

                if (block.Header is null)
                {
                    rows.Add(NotFoundRow(s.DisplayName, "Setting not found in powercfg -q output (may be hidden or unsupported on this Windows build/scheme)."));
                    continue;
                }

                bool acOk = TryReadIndex(block.Body, "AC", out int ac);
                bool dcOk = TryReadIndex(block.Body, "DC", out int dc);
                if (!acOk && !dcOk)
                {
                    rows.Add(NotFoundRow(s.DisplayName, "Couldn't parse the current AC/DC setting index."));
                    continue;
                }

                string valueText = acOk && dcOk ? $"AC {ac} | DC {dc}"
                    : acOk ? $"AC {ac}"
                    : $"DC {dc}";

                rows.Add(new PlatformLatencySettingRow { SettingName = s.DisplayName, ValueText = valueText, Note = s.BalancedDefaultNote });
            }
            return rows;
        }
        catch (Exception ex)
        {
            return Settings.Select(s => NotFoundRow(s.DisplayName, $"Read failed: {ex.Message}")).ToList();
        }
    }

    private static PlatformLatencySettingRow NotFoundRow(string name, string note) =>
        new() { SettingName = name, ValueText = "Unknown", Note = note };

    private static bool TryReadIndex(string body, string acOrDc, out int value)
    {
        var m = Regex.Match(body, $@"Current {acOrDc} Power Setting Index:\s*0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            return true;
        value = 0;
        return false;
    }

    private static readonly Regex SettingHeaderRegex = new(
        @"Power Setting GUID:\s*([0-9a-fA-F-]{36})\s*\(([^)]*)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Splits the full `powercfg -q` dump into (Guid, Header, Body) blocks, body running
    /// until the next such header or end of output - tolerant of the tool's own indentation rather
    /// than assuming a fixed layout (mirrors PowerSchemeInterruptSteeringService.SplitIntoSettingBlocks,
    /// plus the GUID itself for #232's GUID-prefix fallback match).</summary>
    private static List<(string Guid, string Header, string Body)> SplitIntoBlocks(string output)
    {
        var matches = SettingHeaderRegex.Matches(output);
        var blocks = new List<(string, string, string)>();
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            int start = m.Index;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : output.Length;
            blocks.Add((m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim(), output[start..end]));
        }
        return blocks;
    }

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's historical output-only shape (empty string
    /// for a timed-out run).</summary>
    private static async Task<string> RunCapturedAsync(string exe, string args, int timeoutMs = 15000)
        => (await ToolRunner.RunCapturedAsync(exe, args, timeoutMs, timeoutOutput: string.Empty)).Output;
}
