using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #424: loaded kernel module (driver) inventory via NtQuerySystemInformation(SystemModuleInformation) -
/// base address, image size, path, and load order for every currently-loaded driver, the same
/// data WinDbg's `lm` command and `driverquery` show. The raw enumeration only ever gives a file
/// path (no display name), so FriendlyName is filled in afterward from `driverquery /v /fo csv`
/// (matched by file base name) when that succeeds - the same "known tool for the friendly-name
/// gap, raw interop for the enumeration itself" split ScheduledTaskService's XML fetch and this
/// app's other shell-out services already use.
/// </summary>
public static class KernelModuleService
{
    public static async Task<List<KernelModuleRow>> ListAsync()
    {
        var modules = ReadModules();
        if (modules.Count == 0) return modules;

        Dictionary<string, string> friendlyNames;
        try
        {
            friendlyNames = await ReadFriendlyNamesAsync();
        }
        catch
        {
            friendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var m in modules)
        {
            string baseName = Path.GetFileNameWithoutExtension(m.FileName);
            if (friendlyNames.TryGetValue(baseName, out var friendly))
                m.FriendlyName = friendly;
        }

        return modules.OrderByDescending(m => m.ImageSizeBytes).ToList();
    }

    // #424: same growing-buffer NtQuerySystemInformation shape as HandleInspectionService's
    // system-handle-table read and PoolTagInspectionService's pool-tag read - grow until it fits.
    private static List<KernelModuleRow> ReadModules()
    {
        int size = 1 << 20;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(SystemModuleInformation, buffer, size, out int returnLength);
                if (status == StatusInfoLengthMismatch)
                {
                    size = returnLength > size ? returnLength + 0x10000 : size * 2;
                    continue;
                }
                if (status != 0) return new List<KernelModuleRow>();

                return ParseModules(buffer);
            }
            catch
            {
                return new List<KernelModuleRow>();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return new List<KernelModuleRow>();
    }

    private static List<KernelModuleRow> ParseModules(IntPtr buffer)
    {
        var rows = new List<KernelModuleRow>();
        try
        {
            uint count = (uint)Marshal.ReadInt32(buffer);
            int entrySize = Marshal.SizeOf<RTL_PROCESS_MODULE_INFORMATION>();
            // ULONG NumberOfModules header, padded to pointer size before the array (the entry
            // struct has a PVOID member) - array starts at offset 8 on x64, same reasoning as
            // PoolTagInspectionService's SYSTEM_POOLTAG_INFORMATION.
            IntPtr entryPtr = IntPtr.Add(buffer, 8);

            uint safeCount = Math.Min(count, 5000);
            for (uint i = 0; i < safeCount; i++)
            {
                var raw = Marshal.PtrToStructure<RTL_PROCESS_MODULE_INFORMATION>(IntPtr.Add(entryPtr, (int)(i * entrySize)));
                string fullPath = DecodeAnsi(raw.FullPathName);
                if (string.IsNullOrWhiteSpace(fullPath)) continue;

                string fileName = raw.OffsetToFileName < fullPath.Length
                    ? fullPath[raw.OffsetToFileName..]
                    : Path.GetFileName(fullPath);

                rows.Add(new KernelModuleRow
                {
                    FileName = fileName,
                    FullPath = fullPath,
                    BaseAddress = raw.ImageBase.ToInt64(),
                    ImageSizeBytes = raw.ImageSize,
                    LoadOrderIndex = raw.LoadOrderIndex,
                });
            }
        }
        catch
        {
            // Struct layout didn't parse cleanly on this build - return whatever was read so far.
        }
        return rows;
    }

    private static string DecodeAnsi(byte[] bytes)
    {
        int len = Array.IndexOf(bytes, (byte)0);
        if (len < 0) len = bytes.Length;
        return Encoding.ASCII.GetString(bytes, 0, len);
    }

    /// <summary>
    /// #424: shells out to `driverquery /v /fo csv` for a "Display Name" per driver, matched back
    /// to the raw enumeration above by "Module Name" (driverquery's own file-base-name column) -
    /// same concurrent-read/bounded-wait/kill-on-timeout process pattern ScheduledTaskService's
    /// RunCapturedAsync already established, reused here directly rather than re-derived.
    /// </summary>
    private static async Task<Dictionary<string, string>> ReadFriendlyNamesAsync()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo("driverquery.exe", "/v /fo csv")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return result;

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(); } catch { /* best-effort */ }
            return result;
        }

        string output = (await outputTask) + (await errorTask);
        if (proc.ExitCode != 0) return result;

        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) return result;

        var header = ParseCsvLine(lines[0]);
        int iModule = header.FindIndex(h => h.Equals("Module Name", StringComparison.OrdinalIgnoreCase));
        int iDisplay = header.FindIndex(h => h.Equals("Display Name", StringComparison.OrdinalIgnoreCase));
        if (iModule < 0 || iDisplay < 0) return result;

        for (int i = 1; i < lines.Count; i++)
        {
            var fields = ParseCsvLine(lines[i]);
            if (fields.Count <= Math.Max(iModule, iDisplay)) continue;
            string module = fields[iModule].Trim();
            string display = fields[iDisplay].Trim();
            if (module.Length > 0 && display.Length > 0 && !display.Equals(module, StringComparison.OrdinalIgnoreCase))
                result[module] = display;
        }
        return result;
    }

    // driverquery's CSV output quotes every field, same escaping rule as schtasks' own CSV output -
    // ScheduledTaskService.ParseCsvLine duplicated here rather than shared, matching how this
    // app's other services each stay self-contained instead of building a shared CSV helper.
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }

    private const int SystemModuleInformation = 11;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [StructLayout(LayoutKind.Sequential)]
    private struct RTL_PROCESS_MODULE_INFORMATION
    {
        public uint Section;
        public IntPtr MappedBase;
        public IntPtr ImageBase;
        public uint ImageSize;
        public uint Flags;
        public ushort LoadOrderIndex;
        public ushort InitOrderIndex;
        public ushort LoadCount;
        public ushort OffsetToFileName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] FullPathName;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);
}
