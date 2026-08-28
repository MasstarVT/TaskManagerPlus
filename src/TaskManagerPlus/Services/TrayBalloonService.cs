namespace TaskManagerPlus.Services;

/// <summary>#964: a thin static bridge so AlertDeliveryService (Services/, no UI/window dependency
/// of its own) can show a genuine Windows tray balloon for the "TrayBalloon" alert channel without
/// needing a reference to MainWindow. MainWindow.xaml.cs sets Icon once it creates its own
/// NotifyIcon (Round 12, #85's tray icon) and clears it on close; Icon staying null (minimize-to-
/// tray never enabled, or the tray icon itself failed to create - see MainWindow.InitializeTrayIcon)
/// just means TryShowBalloon returns false and AlertDeliveryService falls back to the toast popup
/// instead, rather than silently dropping the alert.</summary>
public static class TrayBalloonService
{
    public static System.Windows.Forms.NotifyIcon? Icon { get; set; }

    public static bool TryShowBalloon(string title, string text, bool isCritical)
    {
        var icon = Icon;
        if (icon is null) return false;
        try
        {
            icon.ShowBalloonTip(8000, title, text, isCritical ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
