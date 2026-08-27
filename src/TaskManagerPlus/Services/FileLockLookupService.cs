using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskManagerPlus.Services;

/// <summary>One process holding a given file open, per the Restart Manager (#9).</summary>
public sealed record FileLockOwner(int Pid, string AppName, bool Restartable);

/// <summary>
/// "What has this file open" (#9) - the classic stuck "file in use, close the program and try
/// again" dialog. Rather than walking the raw system handle table (NtQuerySystemInformation +
/// duplicate-and-query every handle in the system, filtering by object name - expensive, fragile,
/// and prone to hanging on certain handle types), this uses the Restart Manager API
/// (RmStartSession/RmRegisterResources/RmGetList/RmEndSession, rstrtmgr.dll) - the same documented
/// Windows facility behind Explorer's own "This action can't be completed because the file is open
/// in another program" dialog and every installer's "close these programs first" prompt. It's a
/// real, supported API for exactly this question, not a repurposed diagnostic trick.
///
/// Best-effort by design: Restart Manager reports processes holding a *file* handle open (or a DLL
/// loaded from that path), not every possible handle type, and a session can legitimately find
/// nothing even when a lock exists for another reason (a memory-mapped section with no remaining
/// file handle, an in-progress network share operation, ...). An empty result means "Restart
/// Manager found nothing", not a definitive "the file is free".
/// </summary>
public static class FileLockLookupService
{
    public static List<FileLockOwner> FindProcessesWithFileOpen(string path)
    {
        var result = new List<FileLockOwner>();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return result;

        uint session = 0;
        var sessionKey = new StringBuilder(CchRmSessionKey + 1);
        try
        {
            if (RmStartSession(out session, 0, sessionKey) != 0)
                return result;

            var files = new[] { path };
            if (RmRegisterResources(session, (uint)files.Length, files, 0, IntPtr.Zero, 0, null) != 0)
                return result;

            uint procInfoNeeded = 0, procInfo = 0, rebootReasons = 0;
            // First call with a zero-length buffer to find out how many entries are needed.
            uint rc = RmGetList(session, out procInfoNeeded, ref procInfo, null, out rebootReasons);
            if (rc != ErrorMoreData && rc != 0) return result;
            if (procInfoNeeded == 0) return result;

            var infos = new RM_PROCESS_INFO[procInfoNeeded];
            procInfo = procInfoNeeded;
            rc = RmGetList(session, out procInfoNeeded, ref procInfo, infos, out rebootReasons);
            if (rc != 0) return result;

            for (int i = 0; i < procInfo; i++)
            {
                result.Add(new FileLockOwner(
                    infos[i].Process.ProcessId,
                    infos[i].strAppName,
                    infos[i].bRestartable));
            }
        }
        catch
        {
            // rstrtmgr.dll unavailable, or any marshaling failure - an empty list just means
            // "nothing found", the same degrade-gracefully shape as every other lookup in this app.
        }
        finally
        {
            if (session != 0) RmEndSession(session);
        }
        return result.DistinctBy(r => r.Pid).OrderBy(r => r.AppName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private const int CchRmSessionKey = 32;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;
    private const uint ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint dwSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint dwSessionHandle, uint nFiles, string[]? rgsFilenames,
        uint nApplications, IntPtr rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern uint RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, out uint lpdwRebootReasons);
}
