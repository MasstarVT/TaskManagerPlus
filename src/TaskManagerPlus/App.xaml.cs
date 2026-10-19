using System.IO;
using System.Linq;
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
            // Non-zero unless the command completed, so scripts can tell a failure from success
            // (the error itself goes to <path>.error.txt).
            int exitCode = 1;
            try
            {
                await CliDumpService.DumpSnapshotAsync(outputPath);
                exitCode = 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(outputPath + ".error.txt", ex.ToString()); }
                catch { /* best-effort - nothing more useful to do from a CLI path */ }
            }
            Shutdown(exitCode);
            return;
        }

        // suggestions.md #996: `--scan <path>` and `--collect <path>` extend the `--dump-json`
        // pattern above verbatim - same no-UI, construct-fresh-services-and-exit shape, plus
        // `--scrub` (PII-scrub the output, reusing the Evidence Bundle chunk's scrubber) and
        // `--quiet` (both CLI paths already show no UI/dialogs the way --dump-json does, so this
        // is mostly a documented no-op kept for CLI-contract symmetry - see CliDumpService's
        // remarks on each method).
        bool scrub = e.Args.Any(a => a.Equals("--scrub", StringComparison.OrdinalIgnoreCase));
        bool quiet = e.Args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));

        int scanIndex = Array.IndexOf(e.Args, "--scan");
        if (scanIndex >= 0 && scanIndex + 1 < e.Args.Length)
        {
            string outputPath = e.Args[scanIndex + 1];
            int exitCode = 1;
            try
            {
                await CliDumpService.ScanAsync(outputPath, scrub, quiet);
                exitCode = 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(outputPath + ".error.txt", ex.ToString()); }
                catch { /* best-effort - nothing more useful to do from a CLI path */ }
            }
            Shutdown(exitCode);
            return;
        }

        int collectIndex = Array.IndexOf(e.Args, "--collect");
        if (collectIndex >= 0 && collectIndex + 1 < e.Args.Length)
        {
            string outputPath = e.Args[collectIndex + 1];
            int exitCode = 1;
            try
            {
                await CliDumpService.CollectAsync(outputPath, scrub, quiet);
                exitCode = 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(outputPath + ".error.txt", ex.ToString()); }
                catch { /* best-effort - nothing more useful to do from a CLI path */ }
            }
            Shutdown(exitCode);
            return;
        }

        MainWindow window;
        try
        {
            window = new MainWindow();
            window.Show();
        }
        catch (Exception ex)
        {
            // A throw here (e.g. a StaticResource miss while MainWindow's BAML loads) would
            // otherwise be swallowed by OnDispatcherUnhandledException, leaving a never-shown
            // window registered - so OnLastWindowClose never fires and an invisible elevated
            // process keeps every polling timer running with no window or tray icon to close it.
            try
            {
                MessageBox.Show($"Task Manager Plus couldn't open its main window:\n\n{ex.Message}",
                    "Task Manager Plus - Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* reporting is best-effort; shutting down is what matters */ }
            Shutdown(1);
            return;
        }

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

    /// <summary>Last-resort net for an exception that escaped every per-feature try/catch below
    /// it. #1101: that includes an AsyncRelayCommand handler's own throw - Execute is async void
    /// with try/finally but no catch, so a command exception surfaces here via the dispatcher -
    /// as well as any synchronous binding/layout/timer-tick exception. MessageBox.Show is the same
    /// plain, dependency-free reporting mechanism the rest of this app already leans on
    /// (SaveFileDialog, toasts, etc.) - there's no existing logging framework to route this
    /// through instead.</summary>
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
