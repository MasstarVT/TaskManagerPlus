using System.Diagnostics;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #220: parses `powercfg -q` (the active power scheme's full settings dump, the same "known
/// Windows tool, text output parsed" tradeoff PowerPlanService already takes for /list and /a) for
/// the "Interrupt Steering Settings" subgroup, reporting whether the OS is allowed to move interrupt
/// load off busy cores.
///
/// Builds the first row(s) of the Responsiveness tab's "Platform latency settings" card - a small,
/// deliberately generically-named list intended for later chunks in this same domain to append more
/// rows to (see ResponsivenessViewModel.PlatformLatencySettings' remarks).
///
/// This subgroup isn't present in every Windows build/SKU's power scheme (it's most consistently
/// exposed on Server/Datacenter-class builds), and `powercfg -q`'s setting names aren't a
/// documented, versioned contract the way a registry value name is - so this searches the whole
/// dump case-insensitively for a subgroup/setting header containing "Interrupt Steering" rather than
/// assuming a fixed line/indent layout, and reports "Not available on this system" (never a guess)
/// when nothing matching is found at all.
/// </summary>
public static class PowerSchemeInterruptSteeringService
{
    private static readonly Regex SettingHeaderRegex = new(
        @"Power Setting GUID:\s*[0-9a-fA-F-]{36}\s*\(([^)]*)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CurrentAcIndexRegex = new(
        @"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<List<PlatformLatencySettingRow>> ReadInterruptSteeringSettingsAsync()
    {
        try
        {
            string output = await RunCapturedAsync("powercfg.exe", "-q");
            if (string.IsNullOrWhiteSpace(output))
                return new List<PlatformLatencySettingRow> { NotAvailableRow("powercfg -q produced no output") };

            var blocks = SplitIntoSettingBlocks(output);
            if (blocks.Count == 0 || !blocks.Any(b => b.Header.Contains("Interrupt Steering", StringComparison.OrdinalIgnoreCase))
                                    && !output.Contains("Interrupt Steering", StringComparison.OrdinalIgnoreCase))
                return new List<PlatformLatencySettingRow>
                {
                    NotAvailableRow("Not present in this system's active power scheme (this subgroup is most commonly exposed on Server/Datacenter-class Windows builds)."),
                };

            var rows = new List<PlatformLatencySettingRow>
            {
                BuildRow(blocks, "Interrupt steering mode", "steering mode", DecodeSteeringMode),
                BuildRow(blocks, "Unparked time trigger", "unparked", DecodeAsMilliseconds),
                BuildRow(blocks, "Load per core threshold", "load per core", DecodeAsPercent),
            };
            return rows;
        }
        catch (Exception ex)
        {
            return new List<PlatformLatencySettingRow> { NotAvailableRow($"Read failed: {ex.Message}") };
        }
    }

    private static PlatformLatencySettingRow NotAvailableRow(string note) =>
        new() { SettingName = "Interrupt steering mode", ValueText = "Unknown", Note = note };

    private static PlatformLatencySettingRow BuildRow(List<(string Header, string Body)> blocks, string displayName, string matchHint, Func<int, string> decode)
    {
        var block = blocks.FirstOrDefault(b => b.Header.Contains(matchHint, StringComparison.OrdinalIgnoreCase));
        if (block.Header is null)
            return new PlatformLatencySettingRow { SettingName = displayName, ValueText = "Unknown", Note = "Setting not found in powercfg -q output." };

        var m = CurrentAcIndexRegex.Match(block.Body);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out int index))
            return new PlatformLatencySettingRow { SettingName = displayName, ValueText = "Unknown", Note = "Couldn't parse the current setting index." };

        return new PlatformLatencySettingRow { SettingName = displayName, ValueText = decode(index) };
    }

    private static string DecodeSteeringMode(int index) => index switch
    {
        0 => "0 — Off (interrupts stay pinned to their assigned core)",
        1 => "1 — On, default policy",
        2 => "2 — On, aggressive (moves load off busy cores more readily)",
        _ => $"{index} (unrecognized value)",
    };

    private static string DecodeAsMilliseconds(int index) => $"{index} ms";
    private static string DecodeAsPercent(int index) => $"{index}%";

    /// <summary>Splits the full `powercfg -q` dump into (header, body) blocks, one per
    /// "Power Setting GUID: ... (Friendly Name)" entry, body running until the next such header or
    /// end of output - tolerant of the tool's own indentation rather than assuming a fixed layout.</summary>
    private static List<(string Header, string Body)> SplitIntoSettingBlocks(string output)
    {
        var matches = SettingHeaderRegex.Matches(output);
        var blocks = new List<(string, string)>();
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            int start = m.Index;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : output.Length;
            blocks.Add((m.Groups[1].Value.Trim(), output[start..end]));
        }
        return blocks;
    }

    /// <summary>#1084: the shared <see cref="ToolRunner"/> owns the run/capture/kill-on-timeout
    /// mechanism; this wrapper keeps the service's historical output-only shape (empty string
    /// for a timed-out run).</summary>
    private static async Task<string> RunCapturedAsync(string exe, string args, int timeoutMs = 15000)
        => (await ToolRunner.RunCapturedAsync(exe, args, timeoutMs, timeoutOutput: string.Empty)).Output;
}
