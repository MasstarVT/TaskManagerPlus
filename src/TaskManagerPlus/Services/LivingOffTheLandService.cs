using System.Text;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 16, #856: pattern-matches CommandLine (already collected per-tick via WMI in
/// ProcessMonitorService - see GetCommandLineCached) against a handful of well-known
/// "living-off-the-land binary" (LOLBin) command-line shapes: PowerShell obfuscation/stealth flags,
/// a non-standard rundll32 invocation (or one pointing at AppData/Temp), regsvr32's "Squiblydoo"
/// remote-scriptlet pattern, mshta pointed at a URL, certutil abused for download/decode, bitsadmin
/// stealth transfers, and wmic's "process call create" launch technique.
///
/// Cheap: a handful of regex/substring checks, and every one of them short-circuits on a bare
/// process-name comparison before touching the (usually much longer) command-line string - safe for
/// the per-tick poll path per CLAUDE.md's "on-demand vs polled" rule.
///
/// "Quick flag, not a verdict": every one of these binaries has legitimate, common uses (an admin
/// script using -EncodedCommand to avoid quoting headaches, a real installer registering a COM
/// component via regsvr32, ...) - this flags the *shape* of a known abuse pattern, not a confirmed
/// detection.
/// </summary>
public static class LivingOffTheLandService
{
    private static readonly Regex PowerShellStealthFlags = new(
        @"(^|\s)-enc(odedcommand)?\b|(^|\s)-w(indowstyle)?\s+hidden\b|(^|\s)-nop(rofile)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EncodedCommandArgument = new(
        @"-enc(?:odedcommand)?\s+([A-Za-z0-9+/=]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RundllStandardShape = new(
        @"[^,\s]+\.dll\s*,\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Regsvr32SquiblydooPattern = new(
        @"/i:\s*https?://",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WmicProcessCallCreate = new(
        @"process\s+call\s+create",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? Evaluate(string? processName, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        string bareName = StripExeSuffix(processName ?? string.Empty);

        if (IsPowerShell(bareName))
        {
            if (PowerShellStealthFlags.IsMatch(commandLine))
                return "PowerShell was launched with an obfuscation/stealth flag (-EncodedCommand, -WindowStyle Hidden, or -NoProfile) - a common living-off-the-land pattern. Quick flag, not a verdict.";
        }
        else if (bareName.Equals("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            bool looksStandard = RundllStandardShape.IsMatch(commandLine);
            bool inTempOrAppData = commandLine.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) ||
                                    commandLine.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase);
            if (!looksStandard || inTempOrAppData)
                return "rundll32's command line doesn't look like a standard \"DllName,EntryPoint\" call, or points at AppData/Temp - a common living-off-the-land pattern. Quick flag, not a verdict.";
        }
        else if (bareName.Equals("regsvr32", StringComparison.OrdinalIgnoreCase))
        {
            if (Regsvr32SquiblydooPattern.IsMatch(commandLine))
                return "regsvr32 is registering a COM scriptlet fetched from a URL (the \"Squiblydoo\" pattern) - a known living-off-the-land technique. Quick flag, not a verdict.";
        }
        else if (bareName.Equals("mshta", StringComparison.OrdinalIgnoreCase))
        {
            if (commandLine.Contains("http", StringComparison.OrdinalIgnoreCase))
                return "mshta is being pointed at a URL - a known living-off-the-land technique for running remote HTA content. Quick flag, not a verdict.";
        }
        else if (bareName.Equals("certutil", StringComparison.OrdinalIgnoreCase))
        {
            if (commandLine.Contains("-urlcache", StringComparison.OrdinalIgnoreCase) || commandLine.Contains("-decode", StringComparison.OrdinalIgnoreCase))
                return "certutil is being used with -urlcache or -decode - a known living-off-the-land technique for downloading/decoding files outside its normal certificate-management role. Quick flag, not a verdict.";
        }
        else if (bareName.Equals("bitsadmin", StringComparison.OrdinalIgnoreCase))
        {
            if (commandLine.Contains("/transfer", StringComparison.OrdinalIgnoreCase))
                return "bitsadmin /transfer is being used to move a file - a known living-off-the-land technique for a stealthy background download. Quick flag, not a verdict.";
        }
        else if (bareName.Equals("wmic", StringComparison.OrdinalIgnoreCase))
        {
            if (WmicProcessCallCreate.IsMatch(commandLine))
                return "wmic is being used to launch a process (\"process call create\") - a known living-off-the-land technique that bypasses some normal process-creation logging paths. Quick flag, not a verdict.";
        }

        return null;
    }

    /// <summary>#856: extracts the base64 argument from a PowerShell -EncodedCommand/-enc command
    /// line, or null if this isn't PowerShell or no such argument is present - feeds the Processes
    /// tab's "decode" button (enabled only when this returns non-null for SelectedProcess).</summary>
    public static string? TryExtractEncodedCommandArgument(string? processName, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        if (!IsPowerShell(StripExeSuffix(processName ?? string.Empty))) return null;

        var match = EncodedCommandArgument.Match(commandLine);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>#856: decodes a PowerShell -EncodedCommand argument. PowerShell's -EncodedCommand is
    /// documented to be UTF-16LE ("Unicode") base64 specifically - not any other assumed encoding.
    /// Throws on malformed/truncated base64 (Convert.FromBase64String) or invalid UTF-16 sequences -
    /// callers must catch; this is only ever invoked on an explicit user click, never per-tick.</summary>
    public static string DecodeEncodedCommand(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        return Encoding.Unicode.GetString(bytes);
    }

    private static bool IsPowerShell(string bareName) =>
        bareName.Equals("powershell", StringComparison.OrdinalIgnoreCase) || bareName.Equals("pwsh", StringComparison.OrdinalIgnoreCase);

    private static string StripExeSuffix(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
