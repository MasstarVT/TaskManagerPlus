using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 14, items 13-22: reads .dmp files directly - the kernel/complete-dump DUMP_HEADER64
/// format (MEMORY.DMP, and occasionally a Minidump-folder file, depending on the
/// CrashDumpEnabled registry setting - see MinidumpHousekeepingService) and the
/// MINIDUMP_HEADER + stream-directory format (the modern default "small memory dump" under
/// %SystemRoot%\Minidump). Every entry point dispatches on the file's own 4-byte signature
/// rather than assuming one universal layout for both - "PAGE" for the classic header, "MDMP"
/// for a minidump - since which one a given file actually is depends on that registry setting,
/// not on which folder it happens to live in.
///
/// Every read is bounded (header + stream directory + the handful of specific streams this app
/// cares about, never the dump's actual memory contents) and wrapped to degrade to a null/
/// "Unknown"/ParseError field rather than throw or fabricate a value - a cut-short write, a
/// locked file, or an unrecognized/future dump format revision are all real, expected
/// conditions here, the same "quick flag, not a verdict" / "degrade to Unknown, never fabricate"
/// conventions this app already applies to WHEA CPER decoding
/// (EventLogService.DecodeWheaErrorRecord).
/// </summary>
public static class MinidumpParserService
{
    private const int PageHeaderMinSize = 0x2000; // DUMP_HEADER64 is padded to 8KB on disk
    private const int MaxModulesToRead = 2000;     // sanity bound - a real dump's module list is a few hundred at most
    private const int MaxStreamsToRead = 512;      // sanity bound on MINIDUMP_HEADER.NumberOfStreams

    public static string MinidumpFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
    public static string MemoryDmpPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "MEMORY.DMP");
    public static string LiveKernelReportsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "LiveKernelReports");

    // ---------------------------------------------------------------------------------------
    // Items 13-19: Minidump-folder scan + per-file parse.
    // ---------------------------------------------------------------------------------------

    /// <summary>Items 13-19: parses every *.dmp under %SystemRoot%\Minidump.</summary>
    public static List<ParsedDumpInfo> ScanMinidumpFolder()
    {
        var result = new List<ParsedDumpInfo>();
        try
        {
            if (!Directory.Exists(MinidumpFolder)) return result;
            foreach (var file in Directory.GetFiles(MinidumpFolder, "*.dmp"))
                result.Add(ParseDumpFile(file));
        }
        catch
        {
            // Folder missing/access denied - empty list, same as EventLogService.ReadMinidumps.
        }
        return result.OrderByDescending(d => d.FileName).ToList();
    }

    /// <summary>Item 20: %SystemRoot%\MEMORY.DMP presence/size/timestamp, plus the same binary
    /// header parse every Minidump-folder file gets.</summary>
    public static MemoryDumpInfo ReadMemoryDumpInfo()
    {
        try
        {
            var path = MemoryDmpPath;
            if (!File.Exists(path))
                return new MemoryDumpInfo { Exists = false, FilePath = path };

            var info = new FileInfo(path);
            ParsedDumpInfo? parsed = null;
            try { parsed = ParseDumpFile(path); }
            catch { /* size/timestamp are still useful even if the header parse itself fails */ }

            return new MemoryDumpInfo
            {
                Exists = true,
                FilePath = path,
                SizeBytes = info.Length,
                Timestamp = info.LastWriteTime,
                Parsed = parsed,
            };
        }
        catch
        {
            return new MemoryDumpInfo { Exists = false, FilePath = MemoryDmpPath };
        }
    }

    /// <summary>Items 13-18: parses one dump file's header/streams. Never throws - a failure
    /// anywhere in the parse degrades to a ParsedDumpInfo with ParseError set and every other
    /// field left at its default/null, rather than losing the file entirely (the caller still
    /// has its name/size from the surrounding FileInfo read either way).</summary>
    public static ParsedDumpInfo ParseDumpFile(string path)
    {
        var fileInfo = new FileInfo(path);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < 8)
            {
                return new ParsedDumpInfo
                {
                    FilePath = path,
                    FileName = fileInfo.Name,
                    SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
                    ParseError = "File too small to contain a dump header.",
                };
            }

            var sig = new byte[4];
            fs.Read(sig, 0, 4);
            string sigText = Encoding.ASCII.GetString(sig);

            if (sigText == "PAGE")
                return ParsePageFormat(fs, path, fileInfo);
            if (sigText == "MDMP")
                return ParseMinidumpFormat(fs, path, fileInfo);

            return new ParsedDumpInfo
            {
                FilePath = path,
                FileName = fileInfo.Name,
                SizeBytes = fileInfo.Length,
                ParseError = $"Unrecognized dump signature '{sigText}' - not a known DUMP_HEADER64 or MINIDUMP format.",
            };
        }
        catch (Exception ex)
        {
            return new ParsedDumpInfo
            {
                FilePath = path,
                FileName = fileInfo.Name,
                SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
                ParseError = $"Couldn't read dump header: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// Item 13: DUMP_HEADER64 fixed-offset fields - ValidDump (the 4 bytes right after the
    /// "PAGE" signature the caller already matched; expected "DU64" for the 64-bit-only OS this
    /// app targets), MajorVersion/MinorVersion (offsets 0x8/0xC), MachineImageType (0x30),
    /// BugCheckCode (0x38) and its four ULONG64 parameters (0x40/0x48/0x50/0x58, 8-byte aligned
    /// after a 4-byte pad following BugCheckCode) - all well-documented, stable offsets in the
    /// classic kernel/complete-dump header.
    ///
    /// Item 14: DumpType is read from offset 0xF88 - the same field WinDbg's own `!dumpheader`
    /// extension prints as "DumpType" (0x1 = Complete Memory Dump, 0x2 = Kernel Memory Dump).
    /// Only those two well-known values are mapped to a friendly name; anything else shows the
    /// raw header value rather than guessing at a newer Automatic/Active dump variant - those
    /// are actually configured (and distinguishable) via the CrashDumpEnabled registry value
    /// instead of this file-header field - see MinidumpHousekeepingService.
    ///
    /// Item 15: a file shorter than the header's own padded 8KB size, or a ValidDump mismatch,
    /// is flagged IsIncomplete - "incomplete, not analysable" rather than presented as if the
    /// (possibly garbage) fields below were reliable. Note DUMP_HEADER64 carries no module list
    /// itself (PsLoadedModuleList is a live kernel-VA pointer, not something readable from an
    /// offline file without walking paged kernel memory) - item 16's stream/module parsing only
    /// applies to the MINIDUMP format below, so Modules/StreamKinds/BlamedModule stay empty/null
    /// for this format.
    /// </summary>
    private static ParsedDumpInfo ParsePageFormat(FileStream fs, string path, FileInfo fileInfo)
    {
        int toRead = (int)Math.Min(PageHeaderMinSize, fs.Length);
        var header = new byte[toRead];
        fs.Seek(0, SeekOrigin.Begin);
        int read = fs.Read(header, 0, toRead);

        bool incomplete = fs.Length < PageHeaderMinSize;
        string? incompleteReason = incomplete
            ? $"File is only {fs.Length:N0} bytes - shorter than the {PageHeaderMinSize:N0}-byte DUMP_HEADER64, so it was almost certainly cut short before the write finished."
            : null;

        string validDump = read >= 8 ? Encoding.ASCII.GetString(header, 4, 4) : string.Empty;
        if (!incomplete && validDump != "DU64")
        {
            incomplete = true;
            incompleteReason = $"Unexpected ValidDump marker '{validDump}' (expected 'DU64') - header looks corrupt.";
        }

        uint majorVersion = read >= 12 ? BitConverter.ToUInt32(header, 8) : 0;
        uint minorBuild = read >= 16 ? BitConverter.ToUInt32(header, 12) : 0;
        uint machineType = read >= 0x34 ? BitConverter.ToUInt32(header, 0x30) : 0;
        uint bugcheckCode = read >= 0x3C ? BitConverter.ToUInt32(header, 0x38) : 0;

        var parameters = new List<string>();
        if (read >= 0x60)
        {
            parameters.Add($"0x{BitConverter.ToUInt64(header, 0x40):X16}");
            parameters.Add($"0x{BitConverter.ToUInt64(header, 0x48):X16}");
            parameters.Add($"0x{BitConverter.ToUInt64(header, 0x50):X16}");
            parameters.Add($"0x{BitConverter.ToUInt64(header, 0x58):X16}");
        }

        KernelDumpType dumpType = KernelDumpType.Unknown;
        string dumpTypeText = "Unknown (header too short to contain a DumpType field)";
        if (read >= 0xF8C)
        {
            uint rawType = BitConverter.ToUInt32(header, 0xF88);
            (dumpType, dumpTypeText) = rawType switch
            {
                1 => (KernelDumpType.Complete, "Complete memory dump"),
                2 => (KernelDumpType.Kernel, "Kernel memory dump"),
                _ => (KernelDumpType.Unknown, $"Kernel/complete dump (unrecognized header value 0x{rawType:X})"),
            };
        }

        return new ParsedDumpInfo
        {
            FilePath = path,
            FileName = fileInfo.Name,
            SizeBytes = fileInfo.Length,
            Format = "PAGEDU64 (kernel/complete dump)",
            DumpType = dumpType,
            DumpTypeText = dumpTypeText,
            IsIncomplete = incomplete,
            IncompleteReason = incompleteReason,
            OsVersion = majorVersion > 0 ? $"{majorVersion} (build {minorBuild})" : null,
            MachineType = DescribeMachineType(machineType),
            BugcheckCode = bugcheckCode != 0 ? $"0x{bugcheckCode:X8}" : null,
            BugcheckParameters = parameters.ToArray(),
            StreamKinds = new List<string>(),
            Modules = new List<DumpModuleRef>(),
            BlamedModule = null,
            BlamedModuleDossier = null,
        };
    }

    /// <summary>
    /// Item 16: MINIDUMP_HEADER (32 bytes: Signature, Version, NumberOfStreams,
    /// StreamDirectoryRva, CheckSum, TimeDateStamp, Flags) followed by a
    /// MINIDUMP_DIRECTORY[NumberOfStreams] array (12 bytes each: StreamType, DataSize, Rva) -
    /// the standard, publicly documented dbghelp.h minidump format. Walking the directory gives
    /// both the "Contents" stream-kind list and the ModuleList/Exception streams' own RVAs.
    ///
    /// Item 13 (for this format): a kernel-mode minidump has no separately named "bugcheck
    /// stream" in the public MINIDUMP_STREAM_TYPE enum - instead, the same technique third-party
    /// minidump viewers use is applied here: MINIDUMP_EXCEPTION_STREAM.ExceptionRecord.
    /// ExceptionCode holds the bugcheck code and the first four entries of
    /// ExceptionInformation[15] hold its four parameters (see ReadExceptionStream).
    ///
    /// Item 15: an implausible/zero NumberOfStreams, or a stream directory that runs past the
    /// end of the file, marks the dump IsIncomplete before any stream is read at all.
    /// </summary>
    private static ParsedDumpInfo ParseMinidumpFormat(FileStream fs, string path, FileInfo fileInfo)
    {
        fs.Seek(0, SeekOrigin.Begin);
        var header = new byte[32];
        int headerRead = fs.Read(header, 0, 32);
        if (headerRead < 32)
        {
            return new ParsedDumpInfo
            {
                FilePath = path,
                FileName = fileInfo.Name,
                SizeBytes = fileInfo.Length,
                Format = "MDMP (minidump)",
                DumpType = KernelDumpType.Mini,
                DumpTypeText = "Small (Mini)",
                IsIncomplete = true,
                IncompleteReason = "File is shorter than the 32-byte MINIDUMP_HEADER.",
            };
        }

        uint numberOfStreams = BitConverter.ToUInt32(header, 8);
        uint streamDirectoryRva = BitConverter.ToUInt32(header, 0xC);

        bool incomplete = false;
        string? incompleteReason = null;
        if (numberOfStreams == 0 || numberOfStreams > MaxStreamsToRead)
        {
            incomplete = true;
            incompleteReason = numberOfStreams == 0
                ? "Header reports zero streams - no directory to read."
                : $"Header reports an implausible {numberOfStreams} streams.";
        }
        else if ((long)streamDirectoryRva + numberOfStreams * 12L > fs.Length)
        {
            incomplete = true;
            incompleteReason = "Stream directory extends past the end of the file - the write was almost certainly cut short.";
        }

        var streamKinds = new List<string>();
        var modules = new List<DumpModuleRef>();
        string? bugcheckCode = null;
        var parameterStrings = Array.Empty<string>();
        var rawParameters = Array.Empty<ulong>();
        ulong faultAddress = 0;

        if (!incomplete)
        {
            try
            {
                fs.Seek(streamDirectoryRva, SeekOrigin.Begin);
                var dirBuf = new byte[numberOfStreams * 12];
                fs.Read(dirBuf, 0, dirBuf.Length);

                uint moduleListRva = 0, moduleListSize = 0, exceptionRva = 0;
                for (int i = 0; i < numberOfStreams; i++)
                {
                    int off = i * 12;
                    uint streamType = BitConverter.ToUInt32(dirBuf, off);
                    uint dataSize = BitConverter.ToUInt32(dirBuf, off + 4);
                    uint rva = BitConverter.ToUInt32(dirBuf, off + 8);

                    var kind = DescribeStreamType(streamType);
                    if (kind is not null && !streamKinds.Contains(kind)) streamKinds.Add(kind);

                    if (streamType == 4) { moduleListRva = rva; moduleListSize = dataSize; }
                    else if (streamType == 6) { exceptionRva = rva; }
                }

                if (moduleListRva > 0)
                    modules = ReadModuleList(fs, moduleListRva, moduleListSize);

                if (exceptionRva > 0)
                {
                    var (code, ps, addr) = ReadExceptionStream(fs, exceptionRva);
                    if (code != 0)
                    {
                        bugcheckCode = $"0x{code:X8}";
                        rawParameters = ps;
                        parameterStrings = ps.Select(p => $"0x{p:X16}").ToArray();
                        faultAddress = addr;
                    }
                }
            }
            catch (Exception ex)
            {
                // Not marked IsIncomplete - whatever streams/modules were read before the
                // failure are still kept and shown, per this app's "keep whatever partial data
                // is real" convention, rather than discarding a mostly-good parse over one bad
                // stream. The reason still surfaces via ParseError below.
                incompleteReason = $"Stream directory walk failed partway through: {ex.Message}";
            }
        }

        var blameCandidates = new List<ulong>(rawParameters) { faultAddress };
        string? blamed = BlameModule(modules, blameCandidates);
        DriverDossier? dossier = blamed is not null ? ResolveDriverDossier(blamed) : null;

        return new ParsedDumpInfo
        {
            FilePath = path,
            FileName = fileInfo.Name,
            SizeBytes = fileInfo.Length,
            Format = "MDMP (minidump/triage dump)",
            DumpType = KernelDumpType.Mini,
            DumpTypeText = "Small (Mini)",
            IsIncomplete = incomplete,
            IncompleteReason = incompleteReason,
            BugcheckCode = bugcheckCode,
            BugcheckParameters = parameterStrings,
            StreamKinds = streamKinds,
            Modules = modules,
            BlamedModule = blamed,
            BlamedModuleDossier = dossier,
            ParseError = incompleteReason is not null && !incomplete ? incompleteReason : null,
        };
    }

    private static string? DescribeStreamType(uint streamType) => streamType switch
    {
        3 => "ThreadList",
        4 => "ModuleList",
        5 => "MemoryList",
        6 => "Exception",
        7 => "SystemInfo",
        8 => "ThreadExList",
        9 => "Memory64List",
        10 => "CommentA",
        11 => "CommentW",
        12 => "HandleData",
        13 => "FunctionTable",
        14 => "UnloadedModuleList",
        15 => "MiscInfo",
        16 => "MemoryInfoList",
        17 => "ThreadInfoList",
        18 => "HandleOperationList",
        _ => streamType >= 0x8000 ? "VendorSpecific" : null, // 0-2 are Unused/Reserved; 0x8000+ is the vendor-reserved range per MINIDUMP_STREAM_TYPE
    };

    /// <summary>Item 16: MINIDUMP_MODULE_LIST - a ULONG32 count followed by fixed 108-byte
    /// MINIDUMP_MODULE records (BaseOfImage u64 @0, SizeOfImage u32 @8, ModuleNameRva u32 @0x14,
    /// pointing at a length-prefixed UTF-16 MINIDUMP_STRING). Bounded to MaxModulesToRead
    /// records regardless of what the count field claims, so a corrupt count can't turn this
    /// into an unbounded read.</summary>
    private static List<DumpModuleRef> ReadModuleList(FileStream fs, uint rva, uint dataSize)
    {
        var result = new List<DumpModuleRef>();
        try
        {
            fs.Seek(rva, SeekOrigin.Begin);
            var countBuf = new byte[4];
            fs.Read(countBuf, 0, 4);
            uint count = Math.Min(BitConverter.ToUInt32(countBuf, 0), (uint)MaxModulesToRead);

            const int moduleRecordSize = 108;
            var buf = new byte[moduleRecordSize];
            for (int i = 0; i < count; i++)
            {
                fs.Seek(rva + 4 + (long)i * moduleRecordSize, SeekOrigin.Begin);
                int read = fs.Read(buf, 0, moduleRecordSize);
                if (read < moduleRecordSize) break;

                ulong baseOfImage = BitConverter.ToUInt64(buf, 0);
                uint sizeOfImage = BitConverter.ToUInt32(buf, 8);
                uint nameRva = BitConverter.ToUInt32(buf, 0x14);

                string name = ReadMinidumpString(fs, nameRva) ?? $"module_0x{baseOfImage:X}";
                string shortName = name.Contains('\\') ? name[(name.LastIndexOf('\\') + 1)..] : name;

                result.Add(new DumpModuleRef { Name = shortName, BaseAddress = baseOfImage, Size = sizeOfImage });
            }
        }
        catch
        {
            // A malformed module list still leaves whatever modules were read before the failure.
        }
        return result;
    }

    private static string? ReadMinidumpString(FileStream fs, uint rva)
    {
        if (rva == 0) return null;
        try
        {
            long savedPos = fs.Position;
            fs.Seek(rva, SeekOrigin.Begin);
            var lenBuf = new byte[4];
            fs.Read(lenBuf, 0, 4);
            uint lengthBytes = BitConverter.ToUInt32(lenBuf, 0);
            if (lengthBytes == 0 || lengthBytes > 1024)
            {
                fs.Seek(savedPos, SeekOrigin.Begin);
                return lengthBytes == 0 ? string.Empty : null;
            }

            var strBuf = new byte[lengthBytes];
            fs.Read(strBuf, 0, (int)lengthBytes);
            fs.Seek(savedPos, SeekOrigin.Begin);
            return Encoding.Unicode.GetString(strBuf);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Item 13 (minidump/triage format): MINIDUMP_EXCEPTION_STREAM is ThreadId(u32)+
    /// alignment(u32), then a MINIDUMP_EXCEPTION at relative offset 8: ExceptionCode(u32)@0,
    /// ExceptionFlags(u32)@4, ExceptionRecord(u64)@8, ExceptionAddress(u64)@0x10,
    /// NumberParameters(u32)@0x18, alignment(u32)@0x1C, ExceptionInformation[15](u64 each)@0x20.
    /// For a kernel-mode dump, ExceptionCode is the bugcheck code and the first four
    /// ExceptionInformation entries are its four parameters. ExceptionAddress doubles as a
    /// fifth candidate address for item 17's blame scan.</summary>
    private static (uint Code, ulong[] Parameters, ulong FaultAddress) ReadExceptionStream(FileStream fs, uint rva)
    {
        try
        {
            fs.Seek(rva, SeekOrigin.Begin);
            var buf = new byte[8 + 0xA0];
            int read = fs.Read(buf, 0, buf.Length);
            if (read < 8 + 0x20) return (0, Array.Empty<ulong>(), 0);

            uint exceptionCode = BitConverter.ToUInt32(buf, 8);
            ulong exceptionAddress = read >= 8 + 0x18 ? BitConverter.ToUInt64(buf, 8 + 0x10) : 0;
            uint numberParameters = read >= 8 + 0x1C ? Math.Min(BitConverter.ToUInt32(buf, 8 + 0x18), 4u) : 0;

            var parameters = new List<ulong>();
            int infoStart = 8 + 0x20;
            for (int i = 0; i < numberParameters && infoStart + (i + 1) * 8 <= read; i++)
                parameters.Add(BitConverter.ToUInt64(buf, infoStart + i * 8));

            return (exceptionCode, parameters.ToArray(), exceptionAddress);
        }
        catch
        {
            return (0, Array.Empty<ulong>(), 0);
        }
    }

    private static string DescribeMachineType(uint imageType) => imageType switch
    {
        0x8664 => "x64",
        0x014C => "x86",
        0xAA64 => "ARM64",
        0x01C4 => "ARM (Thumb-2)",
        0 => "Unknown",
        _ => $"Unknown (0x{imageType:X})",
    };

    /// <summary>Item 17: "quick flag, not a verdict" (CLAUDE.md) - checks each candidate address
    /// (bugcheck parameters, then the exception record's own fault address) against every loaded
    /// module's [Base, Base+Size) range, returning the name of the first module whose range
    /// contains one. Plain address-range matching, not a symbolised stack.</summary>
    private static string? BlameModule(List<DumpModuleRef> modules, IEnumerable<ulong> candidates)
    {
        if (modules.Count == 0) return null;
        foreach (var addr in candidates)
        {
            if (addr == 0) continue;
            var hit = modules.FirstOrDefault(m => m.Size > 0 && addr >= m.BaseAddress && addr < m.BaseAddress + m.Size);
            if (hit is not null) return hit.Name;
        }
        return null;
    }

    /// <summary>Item 18: FileVersionInfo + signature-check for a driver by module name - checks
    /// %SystemRoot%\System32\drivers\&lt;name&gt; first (where almost all real .sys drivers
    /// live), then %SystemRoot%\System32\&lt;name&gt; (a blamed module is occasionally a core
    /// image like ntoskrnl.exe rather than a driver proper), and gives up with a
    /// ResolvedPath-null dossier when neither exists on this machine.</summary>
    private static DriverDossier ResolveDriverDossier(string moduleName)
    {
        string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] candidates =
        {
            Path.Combine(windir, "System32", "drivers", moduleName),
            Path.Combine(windir, "System32", moduleName),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var vi = FileVersionInfo.GetVersionInfo(candidate);
                DateTime? fileDate = null;
                try { fileDate = File.GetLastWriteTime(candidate); } catch { /* leave null */ }

                return new DriverDossier
                {
                    FileName = moduleName,
                    ResolvedPath = candidate,
                    CompanyName = string.IsNullOrWhiteSpace(vi.CompanyName) ? null : vi.CompanyName.Trim(),
                    ProductName = string.IsNullOrWhiteSpace(vi.ProductName) ? null : vi.ProductName.Trim(),
                    FileVersion = string.IsNullOrWhiteSpace(vi.FileVersion) ? null : vi.FileVersion.Trim(),
                    FileDate = fileDate,
                    SignatureStatus = SignatureCheckService.GetStatus(candidate),
                };
            }
            catch
            {
                // Malformed/inaccessible version resource - fall through to "not found" below
                // rather than surfacing a half-populated dossier for the wrong reason.
            }
        }

        return new DriverDossier { FileName = moduleName, SignatureStatus = "Unknown" };
    }

    // Common Windows core images excluded from "third-party" consideration even when this
    // machine can't resolve a FileVersionInfo for them - avoids treating an unresolvable-but-
    // clearly-Microsoft core image as a "driver present in every crash" false positive.
    private static readonly HashSet<string> KnownMicrosoftCoreImages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntoskrnl.exe", "hal.dll", "halmacpi.dll", "halacpi.dll", "ntkrnlmp.exe", "ntkrpamp.exe",
        "wdf01000.sys", "wdfldr.sys", "win32k.sys", "win32kfull.sys", "win32kbase.sys",
        "ndis.sys", "tcpip.sys", "fltmgr.sys", "ksecdd.sys", "clfs.sys", "cng.sys", "ci.dll",
    };

    /// <summary>Item 19: intersects the non-Microsoft module-name sets of every dump this app
    /// could actually read a module list from - a third-party driver present in every dump on
    /// the machine is the strongest cheap signal available without a real debugger. "Non-
    /// Microsoft" is judged from the on-disk file's own CompanyName (item 18's dossier) when
    /// resolvable, falling back to the small KnownMicrosoftCoreImages exclusion list for the
    /// handful of core images that would otherwise slip through as "unknown vendor". Only
    /// considers dumps with at least one module (so a folder with a single dump, or a folder of
    /// dumps this app could only header-parse, contributes nothing rather than a false "in every
    /// dump" of 1).</summary>
    public static List<CommonDriverRow> FindCommonDrivers(IEnumerable<ParsedDumpInfo> dumps)
    {
        var dumpsWithModules = dumps.Where(d => d.Modules.Count > 0).ToList();
        if (dumpsWithModules.Count < 2) return new List<CommonDriverRow>();

        var perDumpNonMsSets = dumpsWithModules
            .Select(d => new HashSet<string>(
                d.Modules.Select(m => m.Name).Where(n => !IsLikelyMicrosoft(n)),
                StringComparer.OrdinalIgnoreCase))
            .ToList();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in perDumpNonMsSets)
            foreach (var name in set)
                counts[name] = counts.GetValueOrDefault(name) + 1;

        return counts
            .Where(kv => kv.Value == dumpsWithModules.Count)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new CommonDriverRow { Name = kv.Key, DumpCount = kv.Value })
            .ToList();
    }

    private static bool IsLikelyMicrosoft(string moduleName)
    {
        if (KnownMicrosoftCoreImages.Contains(moduleName)) return true;
        var dossier = ResolveDriverDossier(moduleName);
        return dossier.CompanyName is not null && dossier.CompanyName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------
    // Items 21/22: LiveKernelReports scan + WER LiveKernelEvent code join.
    // ---------------------------------------------------------------------------------------

    /// <summary>Item 21: every *.dmp under %SystemRoot%\LiveKernelReports, recursively - each
    /// immediate subfolder is named by the watchdog component that wrote into it (DPCWATCHDOG,
    /// USBHUB3, NDIS, PoW32kWatchdog, VIDEO_ENGINE_TIMEOUT, WATCHDOG, ...), taken as Category
    /// as-is (no attempt to normalize/rename the vendor's own folder name).</summary>
    public static List<LiveKernelReportInfo> ScanLiveKernelReports()
    {
        var result = new List<LiveKernelReportInfo>();
        try
        {
            var root = LiveKernelReportsFolder;
            if (!Directory.Exists(root)) return result;

            foreach (var file in Directory.GetFiles(root, "*.dmp", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    string category = "Unknown";
                    var relative = Path.GetRelativePath(root, file);
                    var firstSegment = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrEmpty(firstSegment) && !string.Equals(firstSegment, info.Name, StringComparison.OrdinalIgnoreCase))
                        category = firstSegment;

                    var (code, desc) = ResolveLiveKernelWerCode(info.Name);

                    result.Add(new LiveKernelReportInfo
                    {
                        FilePath = file,
                        FileName = info.Name,
                        Category = category,
                        SizeBytes = info.Length,
                        Timestamp = info.LastWriteTime,
                        WerCode = code,
                        WerDescription = desc,
                    });
                }
                catch { /* one unreadable file shouldn't stop the rest of the scan */ }
            }
        }
        catch
        {
            // Folder missing/access denied - "no live kernel events found", not an error.
        }
        return result.OrderByDescending(r => r.Timestamp).ToList();
    }

    // Item 22: LiveKernelEvent hex codes -> the same bugcheck-taxonomy names WER/WinDbg use for
    // them - a deliberately small, non-exhaustive table (like BugcheckCodeLookup) of the
    // watchdog-triggered codes a home/desktop user is actually likely to hit.
    private static readonly Dictionary<string, string> LiveKernelEventCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0x141"] = "VIDEO_TDR_TIMEOUT_DETECTED — GPU driver timeout, no reboot",
        ["0x144"] = "VIDEO_ENGINE_TIMEOUT_DETECTED — GPU engine hang, no reboot",
        ["0x117"] = "DXGKRNL_LIVEDUMP — display driver live dump",
        ["0x1a8"] = "SDBUS_INTERNAL_ERROR — storage/SD-bus controller live dump",
        ["0x133"] = "DPC_WATCHDOG_VIOLATION — a DPC or ISR ran far longer than allowed",
        ["0x9e"] = "USER_MODE_HEALTH_MONITOR — a critical user-mode component became unresponsive",
        ["0x15a"] = "OUT_OF_HYPERVISOR_MEMORY — Hyper-V resource exhaustion",
    };

    /// <summary>Item 22: joins a live-kernel .dmp to its WER report folder (ReportArchive or
    /// ReportQueue) by matching the .dmp's own file name against the folder's attached-file
    /// listing - the same "no simple index, scan Report.wer + directory listing" approach
    /// EventLogService.ResolveWerReport already uses for BugCheck 1001's Report Id join. Only
    /// report folders with EventType=LiveKernelEvent are considered.</summary>
    private static (string? Code, string? Description) ResolveLiveKernelWerCode(string dumpFileName)
    {
        try
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var roots = new[]
            {
                Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
                Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
            };

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var werFile = Path.Combine(dir, "Report.wer");
                    if (!File.Exists(werFile)) continue;

                    string text;
                    try { text = File.ReadAllText(werFile); } catch { continue; }

                    if (text.IndexOf("EventType=LiveKernelEvent", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    bool attachedHere;
                    try { attachedHere = Directory.GetFiles(dir).Any(f => string.Equals(Path.GetFileName(f), dumpFileName, StringComparison.OrdinalIgnoreCase)); }
                    catch { attachedHere = false; }
                    if (!attachedHere) continue;

                    var codeMatch = Regex.Match(text, @"^Sig\[0\]\.Value=(0x[0-9A-Fa-f]+)", RegexOptions.Multiline);
                    if (!codeMatch.Success)
                        codeMatch = Regex.Match(text, @"^P1=(0x[0-9A-Fa-f]+)", RegexOptions.Multiline);
                    if (!codeMatch.Success) return (null, null);

                    string code = codeMatch.Groups[1].Value;
                    string description = LiveKernelEventCodes.TryGetValue(code, out var desc) ? desc : $"{code} — unrecognized live-kernel event code";
                    return (code, description);
                }
            }
        }
        catch
        {
            // ReportArchive/ReportQueue missing/access denied - no WER join available for this file.
        }
        return (null, null);
    }
}
