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
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // App-wide safety net: this app has no logging framework, so an unhandled exception on
        // the UI thread otherwise hard-crashes the whole process with no chance to save work
        // (e.g. an in-progress log/snapshot). Surfacing it via MessageBox and marking e.Handled
        // keeps the app alive - the same "quick, honest, no fabricated recovery" spirit every
        // other graceful-degradation path in this app already follows, just for the one failure
        // mode none of those per-feature try/catches can catch.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

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
                CliDumpService.DumpSnapshot(outputPath);
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

    /// <summary>Last-resort net for an exception that escaped every per-handler try/catch below
    /// it (AsyncRelayCommand.Execute's own handlers now catch their own failures - see
    /// Common/RelayCommand.cs's remarks - but a synchronous binding/layout/timer-tick exception
    /// can still reach here). MessageBox.Show is the same plain, dependency-free reporting
    /// mechanism the rest of this app already leans on (SaveFileDialog, toasts, etc.) - there's no
    /// existing logging framework to route this through instead.</summary>
    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            MessageBox.Show(
                $"An unexpected error occurred and has been suppressed so the app can keep running:\n\n{e.Exception.Message}",
                "Task Manager Plus - Unexpected Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // Even the error dialog itself failing shouldn't take the app down.
        }
        finally
        {
            e.Handled = true;
        }
    }
}
