using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TaskManagerPlus.Services;

/// <summary>
/// The one place this always-elevated app starts another program on the user's behalf.
///
/// A plain Process.Start with UseShellExecute=true from an elevated process resolves the target
/// through the same user's HKCU file/protocol associations and App Paths, and through PATH folders
/// such as %LOCALAPPDATA%\Microsoft\WindowsApps - all writable without elevation. Any of them can
/// redirect "open this link/log/console" to a planted command that then runs as Administrator, the
/// same trick the well-known UAC bypasses use. So:
/// - links, documents and Explorer windows open through the unelevated desktop shell (Explorer's
///   own process, via its IShellDispatch2.ShellExecute), never elevated;
/// - Windows' own tools start from their absolute System32 path, with no association lookup;
/// - any other executable starts only when it and every folder above it are writable by
///   SYSTEM/Administrators/TrustedInstaller alone.
/// </summary>
public static class SafeLaunchService
{
    private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>Opens an absolute http/https URL in the user's browser, unelevated. False when the
    /// URL isn't http(s) or the desktop shell isn't available (the caller can offer to copy it).</summary>
    public static bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;
        return ShellExecuteUnelevated(uri.AbsoluteUri, null);
    }

    /// <summary>Opens an existing local document (an .html explainer, a report) with the user's
    /// default handler, unelevated. False for anything that isn't an existing file or is an
    /// executable type.</summary>
    public static bool OpenDocument(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || IsExecutableType(path)) return false;
            return ShellExecuteUnelevated(Path.GetFullPath(path), null);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Shows a file (selected) or folder in an unelevated Explorer window.</summary>
    public static bool RevealInExplorer(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full)) return ShellExecuteUnelevated(Path.Combine(WindowsDir, "explorer.exe"), $"/select,\"{full}\"");
            if (Directory.Exists(full)) return ShellExecuteUnelevated(Path.Combine(WindowsDir, "explorer.exe"), $"\"{full}\"");
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens a text/log file in Notepad from System32 (no file association involved).
    /// Elevated, so it can read admin-only logs such as CBS.log.</summary>
    public static bool OpenInNotepad(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return false;
            return StartSystemTool("notepad.exe", $"\"{Path.GetFullPath(path)}\"");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens a System32 MMC snap-in (e.g. "certlm.msc") through System32\mmc.exe.</summary>
    public static bool OpenSnapIn(string mscFileName)
    {
        if (mscFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !mscFileName.EndsWith(".msc", StringComparison.OrdinalIgnoreCase))
            return false;
        string msc = Path.Combine(System32, mscFileName);
        return File.Exists(msc) && StartSystemTool("mmc.exe", $"\"{msc}\"");
    }

    /// <summary>Starts a Windows tool by file name from System32 (e.g. "devmgmt.msc" is not one -
    /// use OpenSnapIn; "control.exe", "SystemPropertiesProtection.exe" are).</summary>
    public static bool StartSystemTool(string exeFileName, string? arguments = null)
    {
        if (exeFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        string exe = Path.Combine(System32, exeFileName);
        if (!File.Exists(exe)) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(exe, arguments ?? string.Empty) { UseShellExecute = false });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Starts a third-party executable (WinDbg, WPA, ...) only when it and every folder
    /// above it can be modified by SYSTEM/Administrators/TrustedInstaller alone. Null on success,
    /// otherwise the reason it was refused.</summary>
    public static string? StartTrustedExecutable(string exePath, string? arguments = null)
    {
        try
        {
            if (!Path.IsPathFullyQualified(exePath)) return $"{exePath} isn't a full path";
            string full = Path.GetFullPath(exePath);
            if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return $"{full} isn't an .exe";
            string? writable = UnattendedScanService.FindNonAdminWriteAccess(full);
            if (writable is not null) return $"refused to start it elevated: {writable}";
            using var _ = Process.Start(new ProcessStartInfo(full, arguments ?? string.Empty) { UseShellExecute = false });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static bool IsExecutableType(string path)
    {
        string ext = Path.GetExtension(path);
        string[] blocked = { ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
                             ".msc", ".msi", ".msp", ".scr", ".pif", ".lnk", ".url", ".hta", ".cpl", ".reg", ".appref-ms" };
        return blocked.Any(b => ext.Equals(b, StringComparison.OrdinalIgnoreCase));
    }

    // ---- ShellExecute inside the desktop Explorer process (unelevated) ----
    // Raymond Chen's documented technique: find the desktop's shell view through IShellWindows,
    // get its IShellFolderViewDual, and call Application.ShellExecute - the launch happens in
    // Explorer's process with Explorer's (unelevated) token.

    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;
    private const int CSIDL_DESKTOP = 0;
    private const uint SVGIO_BACKGROUND = 0;
    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidDispatch = new("00020400-0000-0000-C000-000000000046");

    internal static bool ShellExecuteUnelevated(string file, string? arguments)
    {
        object? shellWindows = null, desktop = null, browserObj = null, view = null, folderView = null, app = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidShellWindows, throwOnError: false);
            if (type is null) return false;
            shellWindows = Activator.CreateInstance(type);
            if (shellWindows is null) return false;

            // Late-bound (IShellWindows is a dual interface; GetIDsOfNames picks the right DISPID).
            object loc = CSIDL_DESKTOP;
            object? empty = null;
            int hwnd = 0;
            desktop = ((dynamic)shellWindows).FindWindowSW(ref loc, ref empty, SWC_DESKTOP, ref hwnd, SWFO_NEEDDISPATCH);
            if (desktop is not IServiceProvider provider) return false;

            Guid sid = SidTopLevelBrowser, iid = IidShellBrowser;
            if (provider.QueryService(ref sid, ref iid, out browserObj) != 0 || browserObj is not IShellBrowser browser) return false;

            view = browser.QueryActiveShellView();
            if (view is not IShellView shellView) return false;

            Guid dispatch = IidDispatch;
            shellView.GetItemObject(SVGIO_BACKGROUND, ref dispatch, out folderView);
            if (folderView is null) return false;

            app = ((dynamic)folderView).Application;
            if (app is null) return false;
            ((dynamic)app).ShellExecute(file, arguments ?? string.Empty, string.Empty, string.Empty, 1);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (var o in new[] { app, folderView, view, browserObj, desktop, shellWindows })
            {
                if (o is not null && Marshal.IsComObject(o))
                {
                    try { Marshal.ReleaseComObject(o); } catch { /* best-effort */ }
                }
            }
        }
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    }

    // Only the vtable slots up to the one called are declared; their order must match shobjidl.h.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(int fEnterMode);
        void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB(IntPtr pszStatusText);
        void EnableModelessSB(int fEnable);
        void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        void BrowseObject(IntPtr pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        void GetControlWindow(uint id, out IntPtr phwnd);
        void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        [return: MarshalAs(UnmanagedType.Interface)]
        object QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(int fEnterMode);
        void TranslateAccelerator(IntPtr pmsg);
        void EnableModeless(int fEnable);
        void UIActivate(uint uState);
        void Refresh();
        void CreateViewWindow(IntPtr psvPrevious, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phWnd);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr pfs);
        void AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
        void SaveViewState();
        void SelectItem(IntPtr pidlItem, uint uFlags);
        void GetItemObject(uint uItem, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }
}
