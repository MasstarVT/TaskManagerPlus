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

    public static UiPreferences Defaults => new();
}
