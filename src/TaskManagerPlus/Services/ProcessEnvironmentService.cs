using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskManagerPlus.Services;

/// <summary>
/// Best-effort environment-variable reader for an arbitrary already-running process (#3) - .NET's
/// Process class only exposes environment variables for a child process *this app itself* launched
/// (via ProcessStartInfo), not for an arbitrary existing pid, so there's no managed API for this at
/// all. The only way to get it is to walk the target process's own memory: open it, ask
/// NtQueryInformationProcess for its PEB address, read the ProcessParameters pointer out of the PEB,
/// then read the Environment block pointer out of that and parse it as a double-null-terminated
/// sequence of "NAME=VALUE" UTF-16 strings - exactly what tools like Process Explorer's own
/// "Environment" tab do under the hood.
///
/// Deliberately narrow in scope: the PEB/RTL_USER_PROCESS_PARAMETERS field offsets used below are
/// only valid for a 64-bit target process read from this (64-bit) app - a 32-bit/WOW64 target has a
/// separate 32-bit PEB at a different address (reached via ProcessWow64Information, not implemented
/// here) with different offsets. Rather than risk silently reading garbage across that boundary,
/// any target that isn't a same-bitness 64-bit process - or any read that fails for the usual
/// reasons (access denied, protected process, exited mid-read) - degrades to a single explanatory
/// placeholder line, the same "quick best-effort, not guaranteed" tier as
/// CpuTopologyService/NetworkConnectionsService's native calls elsewhere in this app.
/// </summary>
public static class ProcessEnvironmentService
{
    private const int ProcessBasicInformation = 0;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    public static List<string> Read(int pid)
    {
        if (!Environment.Is64BitProcess)
            return new List<string> { "(environment inspection requires a 64-bit build of this app)" };

        IntPtr hProcess = IntPtr.Zero;
        try
        {
            hProcess = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
            if (hProcess == IntPtr.Zero)
                return new List<string> { "(couldn't open the process - access denied or it has already exited)" };

            if (IsWow64(hProcess))
                return new List<string> { "(this process is 32-bit; environment inspection only supports 64-bit processes)" };

            var pbi = new PROCESS_BASIC_INFORMATION();
            int status = NtQueryInformationProcess(hProcess, ProcessBasicInformation, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero)
                return new List<string> { "(couldn't read the process environment block)" };

            // PEB.ProcessParameters is at offset 0x20 on x64.
            IntPtr processParameters = ReadPointer(hProcess, IntPtr.Add(pbi.PebBaseAddress, 0x20));
            if (processParameters == IntPtr.Zero)
                return new List<string> { "(couldn't read process parameters)" };

            // RTL_USER_PROCESS_PARAMETERS.Environment is at offset 0x80 on x64.
            IntPtr environmentBlock = ReadPointer(hProcess, IntPtr.Add(processParameters, 0x80));
            if (environmentBlock == IntPtr.Zero)
                return new List<string> { "(environment block address was empty)" };

            return ReadEnvironmentBlock(hProcess, environmentBlock);
        }
        catch (Exception ex)
        {
            return new List<string> { $"(couldn't read environment: {ex.Message})" };
        }
        finally
        {
            if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
        }
    }

    /// <summary>Reads the double-null-terminated UTF-16 environment block in bounded chunks, capped
    /// well past what any real process's environment block needs, so a corrupt/garbage pointer
    /// can't turn into an unbounded read.</summary>
    private static List<string> ReadEnvironmentBlock(IntPtr hProcess, IntPtr address)
    {
        const int maxBytes = 64 * 1024;
        byte[] buffer = new byte[maxBytes];
        if (!ReadProcessMemory(hProcess, address, buffer, buffer.Length, out int bytesRead) || bytesRead <= 0)
            return new List<string> { "(couldn't read environment memory - it may have been paged out or the process exited)" };

        string raw = Encoding.Unicode.GetString(buffer, 0, bytesRead);
        var entries = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(s => s.Contains('=')) // a non "NAME=VALUE" entry marks the end of real data (truncated read)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return entries.Count > 0
            ? entries
            : new List<string> { "(no environment variables were found, or the block was empty)" };
    }

    private static IntPtr ReadPointer(IntPtr hProcess, IntPtr address)
    {
        byte[] buffer = new byte[IntPtr.Size];
        if (!ReadProcessMemory(hProcess, address, buffer, buffer.Length, out int bytesRead) || bytesRead != IntPtr.Size)
            return IntPtr.Zero;
        return IntPtr.Size == 8 ? (IntPtr)BitConverter.ToInt64(buffer, 0) : (IntPtr)BitConverter.ToInt32(buffer, 0);
    }

    private static bool IsWow64(IntPtr hProcess)
    {
        try
        {
            return IsWow64Process(hProcess, out bool result) && result;
        }
        catch
        {
            return false; // best guess: assume same-bitness rather than refuse outright
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr baseAddress, [Out] byte[] buffer, int size, out int numberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
