using System.IO;

namespace TaskManagerPlus.Services;

/// <summary>
/// Round 14, item 27: watches %SystemRoot%\Minidump and %SystemRoot%\LiveKernelReports for a
/// newly-written *.dmp file while the app is running, so a crash that happened mid-session isn't
/// something the user has to go looking for on their own. NewDumpDetected fires on the
/// FileSystemWatcher's own background thread - callers (StabilityViewModel) marshal back to the
/// UI thread themselves before touching any bound state, the same "background thread -> UI
/// thread hop happens at the call site, not inside the service" shape this app's Task.Run-backed
/// services already use.
/// </summary>
public sealed class NewDumpWatcherService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();

    public event Action<string>? NewDumpDetected; // full file path

    public void Start()
    {
        TryWatch(MinidumpParserService.MinidumpFolder, includeSubdirectories: false);
        TryWatch(MinidumpParserService.LiveKernelReportsFolder, includeSubdirectories: true);
    }

    private void TryWatch(string folder, bool includeSubdirectories)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            var watcher = new FileSystemWatcher(folder, "*.dmp")
            {
                IncludeSubdirectories = includeSubdirectories,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            watcher.Created += (_, e) => SafeRaise(e.FullPath);
            watcher.Renamed += (_, e) => SafeRaise(e.FullPath);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch
        {
            // Folder missing/access denied/can't watch (rare - e.g. a locked-down policy) -
            // simply no live alerting for that folder, the same silent-degrade every other
            // optional feature in this app uses.
        }
    }

    private void SafeRaise(string path)
    {
        try { NewDumpDetected?.Invoke(path); }
        catch { /* a subscriber's own failure shouldn't take the watcher down */ }
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); }
            catch { /* best-effort */ }
        }
        _watchers.Clear();
    }
}
