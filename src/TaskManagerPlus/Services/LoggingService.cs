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
    private StreamWriter? _writer;

    public bool IsLogging => _writer is not null;
    public string? FilePath { get; private set; }

    /// <summary>Opens the file (overwriting any existing content) and writes the header row.
    /// Stops any log already in progress first.</summary>
    public void Start(string path, IReadOnlyList<string> headers)
    {
        Stop();

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _writer = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        FilePath = path;
        _writer.WriteLine(string.Join(",", headers.Select(Escape)));
    }

    /// <summary>No-op when not currently logging, so callers don't need to check IsLogging first.</summary>
    public void WriteRow(IReadOnlyList<string> values)
    {
        if (_writer is null) return;
        _writer.WriteLine(string.Join(",", values.Select(Escape)));
    }

    public void Stop()
    {
        _writer?.Dispose();
        _writer = null;
        FilePath = null;
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public void Dispose() => Stop();
}
