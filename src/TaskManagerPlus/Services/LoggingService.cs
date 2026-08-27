using System.IO;
using System.Text;

namespace TaskManagerPlus.Services;

/// <summary>
/// Writes a running CSV log of monitored values to disk - the same "log everything to one CSV,
/// one row per interval" approach HWiNFO's own logging feature uses. Owns nothing but the open
/// file handle; the caller (LoggingViewModel) decides what columns/values go in each row.
/// </summary>
public sealed class LoggingService : IDisposable
{
    // #74: automatic log rotation - once the active file crosses this size, it's closed and a
    // fresh "-partN" file (same header row) is opened, so an unattended "log everything forever"
    // session doesn't silently fill the disk. 100 MB is roughly a day's worth of this app's own
    // one-row-per-second CSV shape, a reasonable "notice before it gets huge" cap.
    private const long MaxLogFileBytes = 100L * 1024 * 1024;

    private StreamWriter? _writer;
    private List<string> _headers = new();
    private string? _basePath;
    private int _part;

    public bool IsLogging => _writer is not null;
    public string? FilePath { get; private set; }

    /// <summary>Fires after a rotation swaps in a new file - lets the caller re-read FilePath and
    /// update anything displaying it (e.g. the footer's "Stop Logging (filename.csv)" text).</summary>
    public event Action? Rotated;

    /// <summary>Opens the file (overwriting any existing content) and writes the header row.
    /// Stops any log already in progress first.</summary>
    public void Start(string path, IReadOnlyList<string> headers)
    {
        Stop();
        _headers = headers.ToList();
        _basePath = path;
        _part = 1;
        OpenFile(path);
    }

    private void OpenFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        FilePath = path;
        _writer.WriteLine(string.Join(",", _headers.Select(Escape)));
    }

    /// <summary>No-op when not currently logging, so callers don't need to check IsLogging first.</summary>
    public void WriteRow(IReadOnlyList<string> values)
    {
        if (_writer is null) return;
        _writer.WriteLine(string.Join(",", values.Select(Escape)));

        if (_writer.BaseStream.Length >= MaxLogFileBytes && _basePath is not null)
            RotateFile();
    }

    private void RotateFile()
    {
        _writer!.Dispose();
        _part++;
        var dir = Path.GetDirectoryName(_basePath!) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(_basePath!);
        var ext = Path.GetExtension(_basePath!);
        OpenFile(Path.Combine(dir, $"{name}-part{_part}{ext}"));
        Rotated?.Invoke();
    }

    public void Stop()
    {
        _writer?.Dispose();
        _writer = null;
        FilePath = null;
        _basePath = null;
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public void Dispose() => Stop();
}
