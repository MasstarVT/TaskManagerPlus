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

        new MainWindow().Show();
    }
}
