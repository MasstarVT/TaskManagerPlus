using Microsoft.Win32;

namespace TaskManagerPlus.Services;

/// <summary>
/// #404: reads the per-process GDI/USER object handle quotas gdi32/user32 enforce, from
/// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows (GDIProcessHandleQuota/
/// USERProcessHandleQuota) - both values are DWORDs and both default to 10,000 on a stock
/// Windows install when the value is absent, so a missing/denied key degrades to that documented
/// default rather than a guess. Read once and cached (Lazy&lt;int&gt;) since these are effectively
/// static system-wide settings that don't change while the app is running.
/// </summary>
public static class GdiQuotaService
{
    private const int DefaultQuota = 10000;
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";

    private static readonly Lazy<int> GdiQuotaValue = new(() => ReadQuota("GDIProcessHandleQuota"));
    private static readonly Lazy<int> UserQuotaValue = new(() => ReadQuota("USERProcessHandleQuota"));

    public static int GdiQuota => GdiQuotaValue.Value;
    public static int UserQuota => UserQuotaValue.Value;

    private static int ReadQuota(string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            if (key?.GetValue(valueName) is int value && value > 0)
                return value;
        }
        catch
        {
            // Denied or missing key - degrade to the documented Windows default below.
        }
        return DefaultQuota;
    }
}
