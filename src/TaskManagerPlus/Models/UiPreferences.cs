namespace TaskManagerPlus.Models;

/// <summary>Round 11, #80/#81: small app-level window/navigation preferences that don't belong in
/// ThemeColors (they're not colors/visual-scale, they're window behavior and keyboard nav) - same
/// persistence shape as every other settings file in this app
/// (%AppData%\TaskManagerPlus\ui-preferences.json).</summary>
public sealed class UiPreferences
{
    /// <summary>#81: pins the main window above every other window, the same Topmost behavior the
    /// mini dashboard/toast windows already use, just opt-in and user-visible for the main window
    /// itself.</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>#80: which tab each of Ctrl+1..Ctrl+9 jumps to, by tab header text, in order.
    /// Empty (the default) means "use the app's built-in default order" (the first nine tabs in
    /// their normal left-to-right strip order) - MainWindow.xaml.cs falls back to that default
    /// rather than requiring this list to be pre-populated. A plain ordered list is intentionally
    /// simpler than a full remapping UI; edit this file directly to customize which tab each
    /// number opens, the same "no UI, but the underlying JSON is a documented, simple shape"
    /// tradeoff a couple of this app's other advanced options already take.</summary>
    public List<string> TabShortcuts { get; set; } = new();

    /// <summary>Round 12, #85: minimizing the window hides it to the system tray icon instead of
    /// the taskbar - restored by clicking/double-clicking the tray icon or its "Open" menu entry.
    /// Defaults on, matching most other "lives in the tray" Windows monitoring tools; the Settings
    /// drawer lets it be turned off for a plain always-in-the-taskbar minimize instead.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Round 12, #86: opt-out for the Ctrl+Alt+T global "bring to front" hotkey - on by
    /// default (registration itself already degrades gracefully if the combination is taken), but
    /// some users legitimately don't want any background app claiming a global hotkey at all.</summary>
    public bool GlobalHotkeyEnabled { get; set; } = true;

    /// <summary>#991: swaps a Health Check finding's technical Title/Message for its
    /// Rule.PlainEnglishBody-derived alternative wherever one exists (SummaryView's finding rows,
    /// the Markdown/HTML reports, the evidence bundle index.html) - off by default so nothing
    /// changes for anyone who hasn't opted in.</summary>
    public bool PlainEnglishMode { get; set; }

    /// <summary>#999: hard-disables every outbound network call this app can make on its own
    /// (PublicIpLookupService, UpdateCheckService, TracerouteService/ping targets) - see
    /// NetworkActivityCatalogService's remarks for the exact boundary (a user-initiated "Learn
    /// more" browser navigation is deliberately NOT gated by this). Off by default.</summary>
    public bool OfflineMode { get; set; }

    public static UiPreferences Defaults => new();
}
