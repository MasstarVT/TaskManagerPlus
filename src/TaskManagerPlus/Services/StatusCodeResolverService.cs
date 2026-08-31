using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TaskManagerPlus.Services;

/// <summary>
/// #124: resolves a Win32/HRESULT/NTSTATUS status code embedded in an event message to plain text
/// by shelling out to `certutil -error &lt;code&gt;` - the one built-in Windows tool that already
/// decodes all three code families (Win32, HRESULT, and NTSTATUS) in a single call, the same
/// "known tool over raw interop/lookup table" tradeoff this app takes everywhere else (see
/// CLAUDE.md). Results are cached for the lifetime of this service instance, since the same
/// handful of codes (0x80070005 "Access is denied", 0xC0000034 STATUS_OBJECT_NAME_NOT_FOUND, ...)
/// repeat constantly across a busy log. An unresolvable code (certutil missing/blocked, or the code
/// simply isn't recognized) returns null - the caller keeps showing the raw hex/name unchanged,
/// never a guessed meaning.
/// </summary>
public sealed class StatusCodeResolverService
{
    // Deliberately one broad 8-hex-digit pattern rather than three separate ones for
    // 0x8007xxxx/0xC0000xxx/etc. - certutil -error accepts any Win32/HRESULT/NTSTATUS code shaped
    // this way in one call, so there's no need to pre-classify which family a code belongs to.
    private static readonly Regex HexCodeRegex = new(@"0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);
    private static readonly Regex StatusNameRegex = new(@"STATUS_[A-Z_]{3,60}", RegexOptions.Compiled);

    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every distinct 0xNNNNNNNN hex code and STATUS_* token found in <paramref
    /// name="text"/>, in first-seen order - #124's "regex-detect codes inside event messages" half.</summary>
    public static List<string> FindCodes(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in HexCodeRegex.Matches(text))
            if (seen.Add(m.Value)) result.Add(m.Value);
        foreach (Match m in StatusNameRegex.Matches(text))
            if (seen.Add(m.Value)) result.Add(m.Value);
        return result;
    }

    /// <summary>Resolves one code via `certutil -error &lt;code&gt;`. Never throws - a missing
    /// certutil, a timeout, or an unrecognized code all come back as null.</summary>
    public async Task<string?> ResolveAsync(string code)
    {
        if (_cache.TryGetValue(code, out var cached)) return cached;

        string? resolved = await RunCertutilAsync(code);
        _cache[code] = resolved;
        return resolved;
    }

    private static async Task<string?> RunCertutilAsync(string code)
    {
        try
        {
            var (captured, exitCode) = await ToolRunner.RunCapturedAsync("certutil.exe", $"-error {code}", 5000);
            if (exitCode is null) return null;

            string output = captured.Trim();
            if (output.Length == 0) return null;

            // certutil's happy-path output looks like:
            //   0x80070005 (WIN32: 5 ERROR_ACCESS_DENIED)
            //   Access is denied.
            //   CertUtil: -error command completed successfully.
            // Take the first non-empty line that isn't the trailing "CertUtil: ..." status line and
            // isn't just an echo of the code/hex-with-annotation line itself.
            var line = output
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0
                    && !l.StartsWith("CertUtil:", StringComparison.OrdinalIgnoreCase)
                    && !l.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    && !l.Equals(code, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }
        catch
        {
            // certutil.exe missing/blocked, or an unexpected output shape - unresolvable, not fabricated.
            return null;
        }
    }
}
