using System.Diagnostics;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #227: parses `bcdedit /enum {current}` for the handful of boot-config options that affect
/// system timer/clock behavior - useplatformclock (forces HPET/ACPI timer instead of the
/// invariant TSC), useplatformtick, disabledynamictick, tscsyncpolicy, and x2apicpolicy. Same
/// "known tool, text output parsed" tradeoff as every other bcdedit/schtasks/sc-shelled-out
/// service in this app.
///
/// A setting missing from the output means it isn't explicitly overridden - Windows is using its
/// own built-in default for that BCD element, not an error (this app's usual "degrade to
/// Unknown/default, never fabricate" rule) - reported per row as "Not set" with a note on what the
/// Windows default is, rather than a guessed value.
///
/// "Quick flag, not a verdict": forcing useplatformclock/useplatformtick/disabledynamictick on is
/// common online "gaming latency" advice, but on most modern hardware (with a stable, TSC-
/// invariant clock) it typically makes DPC/interrupt latency *worse*, not better, since the
/// platform clock (HPET/ACPI timer) is slower to read than the TSC. This is only ever noted
/// informationally, never as an instruction to change anything.
/// </summary>
public static class BootConfigTimerService
{
    private static readonly (string Key, string DisplayName, string DefaultText, bool FlagWhenTrueLike)[] Settings =
    {
        ("useplatformclock", "Platform clock forced (useplatformclock)",
            "Windows uses the TSC/invariant clock by default.", true),
        ("useplatformtick", "Platform tick forced (useplatformtick)",
            "Windows uses its default tick source.", true),
        ("disabledynamictick", "Dynamic tick disabled (disabledynamictick)",
            "Dynamic tick is enabled by default (fewer timer wake-ups while idle).", true),
        ("tscsyncpolicy", "TSC sync policy (tscsyncpolicy)",
            "Windows picks automatically (\"Default\").", false),
        ("x2apicpolicy", "x2APIC policy (x2apicpolicy)",
            "Windows enables x2APIC automatically when the hardware supports it (\"Default\").", false),
    };

    public static async Task<List<PlatformLatencySettingRow>> ReadAsync()
    {
        var rows = new List<PlatformLatencySettingRow>();
        try
        {
            string output = await RunCapturedAsync("bcdedit.exe", "/enum {current}");
            if (string.IsNullOrWhiteSpace(output))
            {
                foreach (var s in Settings)
                    rows.Add(new PlatformLatencySettingRow { SettingName = s.DisplayName, ValueText = "Unknown", Note = "bcdedit /enum produced no output." });
                return rows;
            }

            foreach (var s in Settings)
            {
                var m = Regex.Match(output, $@"^\s*{Regex.Escape(s.Key)}\s+(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (!m.Success)
                {
                    rows.Add(new PlatformLatencySettingRow { SettingName = s.DisplayName, ValueText = "Not set — using Windows default", Note = s.DefaultText });
                    continue;
                }

                string value = m.Groups[1].Value.Trim();
                bool looksOn = s.FlagWhenTrueLike &&
                    (value.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                     value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                     value.Equals("On", StringComparison.OrdinalIgnoreCase));

                rows.Add(new PlatformLatencySettingRow
                {
                    SettingName = s.DisplayName,
                    ValueText = value,
                    Note = looksOn
                        ? "Guides frequently suggest forcing this on and it usually makes latency worse on modern hardware (informational, not a recommendation to change it)."
                        : null,
                });
            }
        }
        catch (Exception ex)
        {
            foreach (var s in Settings)
                rows.Add(new PlatformLatencySettingRow { SettingName = s.DisplayName, ValueText = "Unknown", Note = $"Read failed: {ex.Message}" });
        }
        return rows;
    }

    private static async Task<string> RunCapturedAsync(string exe, string args, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(exe, args)
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
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { /* best-effort */ }
            return string.Empty;
        }

        return (await outputTask) + (await errorTask);
    }
}
