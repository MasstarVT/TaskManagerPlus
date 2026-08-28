using System.IO;
using System.Windows;
using TaskManagerPlus.Services;

namespace TaskManagerPlus;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// MainWindow is created here instead of via App.xaml's StartupUri, so the `--dump-json`
    /// CLI path (#77) can skip showing any UI at all rather than flashing a window open and
    /// immediately closing it.
    /// </summary>
    // async void is otherwise avoided in this app, but OnStartup is a framework-invoked,
    // event-handler-shaped entry point (like a button click) with no caller waiting on its
    // return - the same reasoning that makes AsyncRelayCommand.Execute itself async void.
    // CliDumpService.DumpSnapshotAsync's awaits resume on WPF's DispatcherSynchronizationContext,
    // which starts pumping messages as soon as this method returns at its first await, so this
    // does not block/deadlock the way a synchronous `.GetAwaiter().GetResult()` call here would.
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Round 12, #87: must run before any service touches a settings file - AppPaths.Initialize
        // decides once, for the whole process, whether persisted state lives under %AppData% or a
        // "Settings" folder next to the exe (portable mode).
        AppPaths.Initialize(e.Args);

        int dumpIndex = Array.IndexOf(e.Args, "--dump-json");
        if (dumpIndex >= 0 && dumpIndex + 1 < e.Args.Length)
        {
            string outputPath = e.Args[dumpIndex + 1];
            try
            {
                await CliDumpService.DumpSnapshotAsync(outputPath);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(outputPath + ".error.txt", ex.ToString()); }
                catch { /* best-effort - nothing more useful to do from a CLI path */ }
            }
            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        window.Show();

        // Round 12, #84: `--tab <name>` opens straight to a given tab (by header text, matching
        // Ctrl+1..9's own header-based lookup in MainWindow.xaml.cs) instead of always landing on
        // Summary - useful for a shortcut/script that always wants the same tab (e.g. a "check my
        // temps" desktop shortcut launching straight to Energy & Thermals).
        int tabIndex = Array.IndexOf(e.Args, "--tab");
        if (tabIndex >= 0 && tabIndex + 1 < e.Args.Length)
        {
            window.SelectTabByName(e.Args[tabIndex + 1]);
        }
    }
}
