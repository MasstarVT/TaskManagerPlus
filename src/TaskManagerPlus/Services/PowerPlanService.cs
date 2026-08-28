using System.Diagnostics;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 12, #90/#91: power scheme listing/switching and Modern Standby (S0) vs. legacy S3 sleep
/// support detection, both by shelling out to powercfg.exe - the same "known Windows tool, not
/// raw registry/API interop" tradeoff ScheduledTaskService/ServiceControlService's recovery-action
/// reader already take (there's no simple public WMI class for either of these; the underlying
/// power-policy API surface is COM-based and a meaningfully larger undertaking for what's
/// ultimately just "read and lightly reformat a command's own text output").
/// </summary>
public static class PowerPlanService
{
    /// <summary>Parses `powercfg /list` - one line per scheme, e.g.
    /// "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced) *" (the trailing `*`
    /// marks the active scheme). Returns an empty list on any failure (powercfg missing/blocked)
    /// rather than throwing - the Energy &amp; Thermals tab just hides the power-plan card when
    /// this comes back empty.</summary>
    public static List<PowerPlanInfo> ListPowerPlans()
    {
        var plans = new List<PowerPlanInfo>();
        try
        {
            string output = RunCaptured("powercfg.exe", "/list").Output;
            foreach (Match m in Regex.Matches(output, @"Power Scheme GUID:\s*([0-9a-fA-F-]{36})\s*\(([^)]*)\)\s*(\*)?"))
            {
                plans.Add(new PowerPlanInfo
                {
                    Guid = m.Groups[1].Value,
                    Name = m.Groups[2].Value.Trim(),
                    IsActive = m.Groups[3].Success,
                });
            }
        }
        catch
        {
            // Best-effort - an empty list just means the power-plan card stays hidden.
        }
        return plans;
    }

    /// <summary>`powercfg /setactive &lt;guid&gt;` - switches the active Windows power scheme.</summary>
    public static (bool Success, string? Error) SetActivePlan(string guid)
    {
        try
        {
            var (output, exitCode) = RunCaptured("powercfg.exe", $"/setactive {guid}");
            return exitCode == 0 ? (true, null) : (false, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Round 12, #91: parses `powercfg /a` ("available sleep states report") to say
    /// whether this system supports Modern Standby (S0 Low Power Idle - the "instant on/off"
    /// networked-standby mode most recent laptops use) vs. legacy S3 ("suspend to RAM", the older
    /// mechanism most desktops still use), or neither/unknown when powercfg's report doesn't
    /// mention either in a recognizable way (older Windows builds phrase this report slightly
    /// differently release to release, so this looks for the two well-known phrases rather than
    /// trying to parse the report's full structure).</summary>
    public static string ReadSleepStateSupport()
    {
        try
        {
            string output = RunCaptured("powercfg.exe", "/a").Output;
            bool hasModernStandby = output.Contains("S0 Low Power Idle", StringComparison.OrdinalIgnoreCase);
            bool hasS3 = Regex.IsMatch(output, @"Standby\s*\(S3\)", RegexOptions.IgnoreCase);

            if (hasModernStandby) return "Modern Standby (S0 Low Power Idle)";
            if (hasS3) return "Legacy S3 (Suspend to RAM)";
            return "Unknown - powercfg /a didn't report a recognizable sleep state";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static (string Output, int ExitCode) RunCaptured(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"couldn't start {exe}");
        string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit(10000);
        return (output, proc.ExitCode);
    }
}
