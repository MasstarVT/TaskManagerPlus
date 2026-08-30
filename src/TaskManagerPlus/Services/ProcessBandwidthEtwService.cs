using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace TaskManagerPlus.Services;

/// <summary>One process's aggregated send/receive byte totals from a single #582 ETW capture
/// window - the real per-process bandwidth measurement the existing #87 connection-count proxy
/// (<see cref="NetworkProcessUsage"/>) explicitly cannot provide, since Windows exposes no public
/// per-process byte-attribution API short of tracing the kernel network stack directly.</summary>
public sealed class EtwProcessBandwidth
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = "Unknown";
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }
    public long TotalBytes => BytesSent + BytesReceived;
}

/// <summary>
/// Item #582 (suggestions.md "Throughput, bufferbloat and per-process bandwidth"): a short,
/// explicitly user-initiated real-time ETW session on the Microsoft-Windows-Kernel-Network
/// provider. Its KERNEL_NETWORK_TASK_TCPIP/UDPIP Send and Recv events carry the owning PID and
/// byte count directly from the kernel network stack (they're logged by the kernel itself, on
/// behalf of whichever process actually owns the socket) - this is genuinely the one place on
/// this tab where CLAUDE.md's "no tool/WMI class gives this" exception to "prefer a known
/// tool/API" applies: there is no netstat-style command or WMI class that attributes bytes to
/// PIDs in real time, only tracing this provider does.
///
/// Uses Microsoft.Diagnostics.Tracing.TraceEvent (the same third-party-NuGet-dependency tradeoff
/// this project already accepts for LibreHardwareMonitorLib, per CLAUDE.md's Sensors note) rather
/// than hand-rolling ETW's manifest/TDH event decoding from scratch. Microsoft-Windows-Kernel-
/// Network is a normal manifest-based provider (not the classic monolithic NT Kernel Logger), so
/// a plain named TraceEventSession + DynamicTraceEventParser (exposed as
/// <c>session.Source.Dynamic.All</c>) decodes its payload fields by name using the provider's
/// manifest already registered in Windows - this app never needs its own copy of that manifest.
///
/// A real-time ETW session needs an elevated process (this app always runs elevated - see
/// CLAUDE.md's Elevation note) and claims a machine-wide named kernel-mode session; <see
/// cref="Start"/> degrades to a clear failure message (never a crash) on a permission failure or
/// a stale session left behind by a previous crashed run, per CLAUDE.md's "degrade to
/// Unknown/0/hidden, never fabricate" convention - it never silently reports zero bytes as if
/// that were a real measurement.
/// </summary>
public sealed class ProcessBandwidthEtwService : IDisposable
{
    private const string SessionName = "TaskManagerPlus-ProcessBandwidth";
    private const string ProviderName = "Microsoft-Windows-Kernel-Network";
    private static readonly Guid ProviderGuid = Guid.Parse("7dd42a49-5329-4832-8dfd-43d979153a88");

    private readonly object _lock = new();
    private readonly Dictionary<int, EtwProcessBandwidth> _totals = new();
    private TraceEventSession? _session;
    private Thread? _processingThread;

    public bool IsCapturing { get; private set; }
    public DateTime? CaptureStartedUtc { get; private set; }

    /// <summary>Starts a new capture. Returns null on success, or a human-readable failure reason
    /// (surfaced verbatim in the view's status text) if the session couldn't be created - most
    /// commonly a leftover session with the same name from a previous crashed run, which this
    /// tries to clear first since ETW session names are machine-wide and a stale one blocks a
    /// fresh CreateTrace with ERROR_ALREADY_EXISTS.</summary>
    public string? Start()
    {
        if (IsCapturing) return null;

        try
        {
            if (TraceEventSession.GetActiveSessionNames().Contains(SessionName, StringComparer.OrdinalIgnoreCase))
            {
                using var stale = new TraceEventSession(SessionName) { StopOnDispose = true };
                stale.Stop();
            }
        }
        catch
        {
            // Best-effort cleanup - if this fails, the real error surfaces from the CreateTrace below.
        }

        lock (_lock) _totals.Clear();

        try
        {
            var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableProvider(ProviderName, TraceEventLevel.Informational);
            session.Source.Dynamic.All += OnEvent;
            _session = session;

            _processingThread = new Thread(() =>
            {
                try { session.Source.Process(); } // blocks until the session is stopped
                catch { /* the session was torn down out from under Process() - expected on Stop() */ }
            })
            { IsBackground = true, Name = "TMP-EtwBandwidth" };
            _processingThread.Start();

            IsCapturing = true;
            CaptureStartedUtc = DateTime.UtcNow;
            return null;
        }
        catch (Exception ex)
        {
            try { _session?.Dispose(); } catch { /* best-effort */ }
            _session = null;
            IsCapturing = false;
            return ex.Message;
        }
    }

    private void OnEvent(TraceEvent data)
    {
        try
        {
            if (data.ProviderGuid != ProviderGuid) return;

            string task = data.TaskName ?? string.Empty;
            bool isTcpOrUdp = task.Contains("TCPIP", StringComparison.OrdinalIgnoreCase) || task.Contains("UDPIP", StringComparison.OrdinalIgnoreCase);
            if (!isTcpOrUdp) return;

            string opcode = data.OpcodeName ?? string.Empty;
            bool isSend = opcode.Contains("Send", StringComparison.OrdinalIgnoreCase);
            bool isRecv = !isSend && opcode.Contains("Recv", StringComparison.OrdinalIgnoreCase);
            if (!isSend && !isRecv) return;

            long? pidField = ReadNumericField(data, "PID", "pid", "ProcessId");
            int pid = pidField is > 0 ? (int)pidField.Value : data.ProcessID;
            if (pid <= 0) return;

            long size = ReadNumericField(data, "size", "Size", "MessageSize", "NumBytes") ?? 0;
            if (size <= 0) return;

            lock (_lock)
            {
                if (!_totals.TryGetValue(pid, out var row))
                {
                    row = new EtwProcessBandwidth { Pid = pid, ProcessName = ResolveProcessName(pid) };
                    _totals[pid] = row;
                }
                if (isSend) row.BytesSent += size; else row.BytesReceived += size;
            }
        }
        catch
        {
            // One malformed/unexpected event shouldn't kill the whole capture.
        }
    }

    private static long? ReadNumericField(TraceEvent data, params string[] candidateNames)
    {
        foreach (var name in candidateNames)
        {
            try
            {
                var value = data.PayloadByName(name);
                if (value is null) continue;
                return Convert.ToInt64(value);
            }
            catch
            {
                // Field absent under this name (schema differs across Windows builds), or not
                // convertible - fall through to the next candidate name.
            }
        }
        return null;
    }

    /// <summary>#1087: delegates to the shared <see cref="ProcessNameLookup"/> (which disposes the
    /// Process object - the local copy here leaked it), keeping this service's ".exe" suffix and
    /// "PID {pid}" fallback for a process that already exited or can't be queried.</summary>
    private static string ResolveProcessName(int pid)
        => ProcessNameLookup.TryGetProcessName(pid) is { } name ? name + ".exe" : $"PID {pid}";

    /// <summary>Stops the capture and returns the final per-process totals, sorted by total bytes
    /// descending - the snapshot #583 persists into history and the "Per-process bandwidth
    /// (ETW capture)" section lists.</summary>
    public List<EtwProcessBandwidth> Stop()
    {
        if (!IsCapturing) return new List<EtwProcessBandwidth>();
        IsCapturing = false;
        CaptureStartedUtc = null;

        try { if (_session is not null) _session.Source.Dynamic.All -= OnEvent; } catch { /* best-effort */ }
        try { _session?.Stop(); } catch { /* best-effort */ }
        try { _processingThread?.Join(TimeSpan.FromSeconds(3)); } catch { /* best-effort */ }
        try { _session?.Dispose(); } catch { /* best-effort */ }
        _session = null;
        _processingThread = null;

        lock (_lock)
            return _totals.Values.OrderByDescending(t => t.TotalBytes).ToList();
    }

    public void Dispose()
    {
        if (IsCapturing) Stop();
    }
}
