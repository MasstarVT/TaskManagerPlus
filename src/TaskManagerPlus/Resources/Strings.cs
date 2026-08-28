using System.Globalization;
using System.Resources;

namespace TaskManagerPlus.Resources;

/// <summary>
/// Round 12, #89: resource-file scaffolding for future localization, while only English ships
/// today. Deliberately hand-written rather than relying on Visual Studio's ResXFileCodeGenerator
/// custom tool (which needs the IDE, not just `dotnet build`, to regenerate a Designer.cs) - a
/// small static wrapper over a plain <see cref="ResourceManager"/> gets the same
/// "strongly-named accessor over Strings.resx" result with zero extra build tooling, and reads
/// the current UI culture on every call (<see cref="CultureInfo.CurrentUICulture"/>) so a future
/// translated .resx (e.g. Strings.de.resx) would satellite-assembly-resolve automatically with no
/// code change here.
///
/// This is intentionally scoped to a handful of strings (see Strings.resx's own remarks) - a few
/// footer/header labels, not a full extraction of every string in this app's XAML. Treat this as
/// the proven pattern for a future round to extend, not a finished localization pass.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager = new("TaskManagerPlus.Resources.Strings", typeof(Strings).Assembly);

    private static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string AppTitle => Get("AppTitle");
    public static string SettingsButton => Get("SettingsButton");
    public static string StartLoggingButton => Get("StartLoggingButton");
    public static string MiniDashboardOpenButton => Get("MiniDashboardOpenButton");
    public static string MiniDashboardCloseButton => Get("MiniDashboardCloseButton");
    public static string ElevatedStatus => Get("ElevatedStatus");
    public static string NotElevatedStatus => Get("NotElevatedStatus");
}
