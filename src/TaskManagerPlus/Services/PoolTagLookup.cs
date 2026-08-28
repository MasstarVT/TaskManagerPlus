using System.Diagnostics;
using System.IO;
using System.Text;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 15, item 35: best-effort pool-tag -&gt; owning-driver resolution for BAD_POOL_CALLER
/// (0xC2) and DRIVER_CORRUPTED_EXPOOL (0xC5), whose parameters can carry a 4-character ASCII pool
/// tag packed into a 32-bit value (the same tag a driver's own ExAllocatePoolWithTag call
/// passed). Two independent, both explicitly best-effort, resolution sources - a pooltag.txt
/// shipped with the Debugging Tools for Windows when installed (the real Microsoft-maintained
/// tag-&gt;driver mapping list, checked first since it's authoritative when present), falling
/// back to a bounded raw-byte scan of driver binaries under System32\drivers for the literal tag
/// bytes (the tag is usually a string literal baked into the driver's own .rdata, from its own
/// ExAllocatePoolWithTag call sites) - never a full disassembly, and bounded by both a file-count
/// cap and a wall-clock budget so a large drivers folder can't turn a tab refresh into a long
/// stall. Either source can turn up nothing; this always degrades to "not found" rather than
/// guessing (CLAUDE.md's "degrade to Unknown, never fabricate").
/// </summary>
public static class PoolTagLookup
{
    private const int MaxDriverFilesToScan = 250;
    private const long MaxBytesPerFile = 4 * 1024 * 1024; // 4MB - comfortably covers a real .sys's .rdata section
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(4);

    public static bool AppliesTo(uint bugcheckCode) => bugcheckCode == 0x000000C2 || bugcheckCode == 0x000000C5;

    /// <summary>Scans every bugcheck parameter for a plausible 4-byte ASCII pool tag (both byte
    /// orders tried, since which one prints "naturally" depends on how the driver's own source
    /// phrased its string literal) - returns the first one found, or null when nothing in the
    /// parameter list looks like a printable tag at all.</summary>
    public static string? TryExtractTag(IReadOnlyList<string> parameters)
    {
        foreach (var p in parameters)
        {
            if (!BugcheckHex.TryParse(p, out var full)) continue;
            uint v = unchecked((uint)(full & 0xFFFFFFFF));
            if (v == 0) continue;

            var direct = BytesToTagIfPrintable(v, reversed: false);
            if (direct is not null) return direct;
            var reversed = BytesToTagIfPrintable(v, reversed: true);
            if (reversed is not null) return reversed;
        }
        return null;
    }

    private static string? BytesToTagIfPrintable(uint value, bool reversed)
    {
        var bytes = BitConverter.GetBytes(value); // little-endian on every platform this app runs on
        if (reversed) Array.Reverse(bytes);
        if (bytes.Any(b => b < 0x20 || b > 0x7E)) return null;
        return Encoding.ASCII.GetString(bytes);
    }

    /// <summary>Resolves a tag to a likely owning driver name. Returns (null, "not found") when
    /// neither source turned up anything - a real, expected outcome, not a failure.</summary>
    public static (string? Driver, string Source) Resolve(string tag)
    {
        var fromFile = TryResolveFromPoolTagTxt(tag);
        if (fromFile is not null) return (fromFile, "pooltag.txt");

        var fromScan = TryResolveFromDriverScan(tag);
        return fromScan is not null ? (fromScan, "driver binary scan") : (null, "not found");
    }

    private static string? TryResolveFromPoolTagTxt(string tag)
    {
        try
        {
            foreach (var candidate in FindPoolTagTxtCandidates())
            {
                if (!File.Exists(candidate)) continue;
                foreach (var line in File.ReadLines(candidate))
                {
                    // pooltag.txt's own format is "Tag  - Binary - Description", the tag left-
                    // justified and space-padded to 4 characters.
                    if (line.Length < 4) continue;
                    var lineTag = line[..4];
                    if (lineTag != tag && !string.Equals(lineTag.Trim(), tag.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;

                    var dashIndex = line.IndexOf('-');
                    if (dashIndex < 0) continue;
                    var rest = line[(dashIndex + 1)..].Trim();
                    var nextDash = rest.IndexOf('-');
                    var binary = (nextDash >= 0 ? rest[..nextDash] : rest).Trim();
                    if (!string.IsNullOrEmpty(binary)) return binary;
                }
            }
        }
        catch
        {
            // pooltag.txt missing/unreadable - fall through to the driver-binary scan below.
        }
        return null;
    }

    private static IEnumerable<string> FindPoolTagTxtCandidates()
    {
        string[] roots =
        {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Windows Kits\10\Debuggers\x64"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Windows Kits\10\Debuggers\x64"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Windows Kits\10\Debuggers\x86"),
        };
        foreach (var root in roots)
        {
            yield return Path.Combine(root, "triage", "pooltag.txt");
            yield return Path.Combine(root, "..", "..", "Triage", "pooltag.txt");
        }
    }

    private static string? TryResolveFromDriverScan(string tag)
    {
        try
        {
            var driversDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers");
            if (!Directory.Exists(driversDir)) return null;

            var tagBytesForward = Encoding.ASCII.GetBytes(tag);
            var tagBytesReversed = (byte[])tagBytesForward.Clone();
            Array.Reverse(tagBytesReversed);

            var sw = Stopwatch.StartNew();
            int scanned = 0;
            foreach (var file in Directory.EnumerateFiles(driversDir, "*.sys"))
            {
                if (scanned >= MaxDriverFilesToScan || sw.Elapsed > ScanBudget) break;
                scanned++;
                try
                {
                    using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    long toRead = Math.Min(fs.Length, MaxBytesPerFile);
                    var buffer = new byte[toRead];
                    int read = fs.Read(buffer, 0, (int)toRead);
                    if (ContainsSequence(buffer, read, tagBytesForward) || ContainsSequence(buffer, read, tagBytesReversed))
                        return Path.GetFileName(file);
                }
                catch
                {
                    // locked/inaccessible file - skip it, the scan continues with the rest.
                }
            }
        }
        catch
        {
            // System32\drivers missing/access denied - "not found" via the driver scan too.
        }
        return null;
    }

    private static bool ContainsSequence(byte[] buffer, int length, byte[] pattern)
    {
        if (pattern.Length == 0 || length < pattern.Length) return false;
        for (int i = 0; i <= length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (buffer[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}
