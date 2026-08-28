using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #416/#417/#418: reads the per-tag kernel pool allocation table via
/// NtQuerySystemInformation(SystemPoolTagInformation) - the same data poolmon.exe shows, without
/// requiring the WDK to be installed - then joins in (#417) a best-effort "which driver likely
/// owns this tag" guess and (#418) a curated description, so the raw four-character tags actually
/// mean something.
///
/// The raw tag read (#416) is cheap - one syscall, a few hundred KB. The driver-attribution pass
/// (#417) is not: it's the manual `findstr /m /l &lt;tag&gt; *.sys` step of the standard
/// pool-leak-diagnosis workflow, automated by scanning every driver image under
/// %SystemRoot%\System32\drivers plus every currently-loaded module's own path for the literal
/// 4-byte tag - hundreds of files, tens of MB of I/O. Results are cached to JSON
/// (PoolTagDriverCacheService) since re-scanning on every #416 refresh would make the "Scan pool
/// tags" button unusably slow; a cached attribution is reused until the user explicitly re-scans
/// (button-gated, never on a tick - see MemoryViewModel).
/// </summary>
public static class PoolTagInspectionService
{
    // #416: bounded the same way every other growing-buffer NtQuerySystemInformation caller in
    // this app is (see HandleInspectionService.ReadSystemHandles) - grow until it fits, or give up.
    public static List<PoolTagRow> ReadPoolTags()
    {
        int size = 1 << 20;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemPoolTagInformation, buffer, size, out int returnLength);
                if (status == StatusInfoLengthMismatch)
                {
                    size = returnLength > size ? returnLength + 0x10000 : size * 2;
                    continue;
                }
                if (status != 0) return new List<PoolTagRow>();

                return ParsePoolTags(buffer);
            }
            catch
            {
                return new List<PoolTagRow>();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return new List<PoolTagRow>();
    }

    private static List<PoolTagRow> ParsePoolTags(IntPtr buffer)
    {
        var rows = new List<PoolTagRow>();
        try
        {
            uint count = (uint)Marshal.ReadInt32(buffer);
            int entrySize = Marshal.SizeOf<SYSTEM_POOLTAG>();
            // The header is one ULONG (Count) but SYSTEM_POOLTAG needs pointer-size alignment (it
            // has a SIZE_T member), so the array itself starts 8 bytes in on x64, not 4.
            IntPtr entryPtr = IntPtr.Add(buffer, 8);

            // Sanity cap - a genuinely corrupt/misread count shouldn't turn into an
            // out-of-bounds read loop; a real system rarely has more than a few thousand tags.
            uint safeCount = Math.Min(count, 20000);
            for (uint i = 0; i < safeCount; i++)
            {
                var raw = Marshal.PtrToStructure<SYSTEM_POOLTAG>(IntPtr.Add(entryPtr, (int)(i * entrySize)));
                rows.Add(new PoolTagRow
                {
                    Tag = TagToString(raw.TagUlong),
                    PagedAllocs = (int)raw.PagedAllocs,
                    PagedFrees = (int)raw.PagedFrees,
                    PagedBytes = (long)raw.PagedUsed,
                    NonpagedAllocs = (int)raw.NonPagedAllocs,
                    NonpagedFrees = (int)raw.NonPagedFrees,
                    NonpagedBytes = (long)raw.NonPagedUsed,
                });
            }
        }
        catch
        {
            // Struct layout didn't parse cleanly on this build - return whatever was read so far
            // (possibly nothing), same "degrade, don't throw" contract as everywhere else.
        }
        return rows;
    }

    /// <summary>The 4 raw tag bytes, taken low-byte-first (matches the union's byte[4]/ULONG
    /// layout on little-endian x64) - non-printable bytes (rare, but some tags intentionally set
    /// the high bit to mark a "protected"/renamed tag) render as '.' rather than a control
    /// character, purely for display.</summary>
    private static string TagToString(uint tagUlong)
    {
        var bytes = BitConverter.GetBytes(tagUlong);
        var chars = new char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(bytes[i] & 0x7F);
            chars[i] = b is >= 0x20 and < 0x7F ? (char)b : '.';
        }
        return new string(chars);
    }

    // #418: lazily parsed once per process lifetime - the embedded file never changes at runtime.
    private static Dictionary<string, string>? _dictionaryCache;

    /// <summary>#418: looks up a tag's curated description ("MmSt" -> "Mm section object
    /// prototype PTEs (memory manager)") - null when the tag isn't in this deliberately partial
    /// built-in dictionary, never a guessed value. See Resources\pooltag.txt.</summary>
    public static string? LookupDescription(string tag)
    {
        var dict = _dictionaryCache ??= LoadDictionary();
        return dict.TryGetValue(tag, out var desc) ? desc : null;
    }

    private static Dictionary<string, string> LoadDictionary()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("pooltag.txt");
            if (stream is null) return dict;
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int eq = line.IndexOf('=');
                if (eq < 4) continue; // need at least a 4-char tag before '='
                string tag = line[..eq];
                if (tag.Length != 4) continue;
                string description = line[(eq + 1)..].Trim();
                if (description.Length == 0) continue;
                dict[tag] = description; // last entry for a duplicate tag wins - see the file's own header
            }
        }
        catch
        {
            // Missing/corrupt embedded resource - degrade to "no descriptions available", never throw.
        }
        return dict;
    }

    // #417: driver-attribution scan - bounded the same "deadline + cap" way SharedMemoryInspectionService
    // bounds its own heavier system-wide scan, since this walks the contents of every driver file on
    // disk rather than just in-memory structures.
    private const int MaxDriverFilesToScan = 2000;
    private const long MaxFileBytesToScan = 64L * 1024 * 1024; // skip absurdly large driver images
    private static readonly TimeSpan AttributionOverallTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// #417: for each of the given tags, scans every .sys file under %SystemRoot%\System32\drivers
    /// (and any additional loaded-module paths supplied) for the literal 4-byte tag string -
    /// exactly the manual `findstr /m /l &lt;tag&gt; *.sys` step of the standard pool-leak-diagnosis
    /// workflow, just automated across every tag at once per file read (one read per file, checked
    /// against every still-unmatched tag, rather than one file pass per tag). A tag can appear in
    /// more than one driver file (shared headers, statically-linked libraries) - the first file
    /// found wins, which is a real limitation of this technique, not just this implementation
    /// (same caveat findstr itself has).
    /// </summary>
    public static Dictionary<string, string> ScanForDriverAttribution(IEnumerable<string> tags, IEnumerable<string> extraModulePaths)
    {
        var remaining = new HashSet<string>(tags, StringComparer.Ordinal);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (remaining.Count == 0) return found;

        var deadline = DateTime.UtcNow + AttributionOverallTimeout;

        var files = new List<string>();
        try
        {
            var driversDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers");
            if (Directory.Exists(driversDir))
                files.AddRange(Directory.EnumerateFiles(driversDir, "*.sys", SearchOption.TopDirectoryOnly));
        }
        catch { /* best-effort */ }

        try
        {
            foreach (var path in extraModulePaths)
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && !files.Contains(path, StringComparer.OrdinalIgnoreCase))
                    files.Add(path);
            }
        }
        catch { /* best-effort */ }

        int scanned = 0;
        foreach (var file in files)
        {
            if (remaining.Count == 0 || scanned >= MaxDriverFilesToScan || DateTime.UtcNow > deadline) break;
            scanned++;

            byte[] bytes;
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytesToScan) continue;
                bytes = File.ReadAllBytes(file);
            }
            catch
            {
                continue; // locked/inaccessible file - skip, not fatal
            }

            string driverName = Path.GetFileNameWithoutExtension(file);
            var matchedThisFile = new List<string>();
            foreach (var tag in remaining)
            {
                var needle = System.Text.Encoding.ASCII.GetBytes(tag);
                if (ContainsBytes(bytes, needle))
                    matchedThisFile.Add(tag);
            }
            foreach (var tag in matchedThisFile)
            {
                found[tag] = driverName;
                remaining.Remove(tag);
            }
        }

        return found;
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        int last = haystack.Length - needle.Length;
        for (int i = 0; i <= last; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    private const int SystemPoolTagInformation = 22;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POOLTAG
    {
        public uint TagUlong;
        public uint PagedAllocs;
        public uint PagedFrees;
        public UIntPtr PagedUsed;
        public uint NonPagedAllocs;
        public uint NonPagedFrees;
        public UIntPtr NonPagedUsed;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);
}
