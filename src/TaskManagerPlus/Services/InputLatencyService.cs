using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #256/#257: registers for system-wide raw input (RegisterRawInputDevices/WM_INPUT, user32.dll)
/// on a hidden message-only window and compares each event's arrival against the message's own
/// queue timestamp to measure how long input sat in the OS queue before delivery, plus derives the
/// actual mouse/keyboard report interval from the raw-input timestamps themselves. No tool/WMI
/// equivalent exists for either, so raw P/Invoke is the documented exception here (same tier as
/// HungWindowService) - the message-hook plumbing follows GlobalHotkeyService's own HwndSource
/// idiom, except this uses a dedicated message-only window (HWND_MESSAGE, never shown) rather than
/// hooking the main window, so it can be started/stopped independently of the app's own UI.
///
/// #256: WM_INPUT's own arrival is compared against GetMessageTime() - the OS timestamp (ms since
/// boot, same clock base as GetTickCount) of when the message was actually posted to this thread's
/// queue - giving the OS-queue delay directly, without needing to reconcile QPC against a
/// different clock base. Granularity is bounded by GetTickCount's own ~15.6ms resolution (or finer
/// if some other app has raised the system timer resolution - see TimerResolutionService); this is
/// a quick flag on queueing delay, not a sub-millisecond instrument.
///
/// #257: the true hardware polling interval is approximated from the tightest back-to-back
/// intervals actually observed per device class (mouse/keyboard) - a genuinely idle period between
/// reports (the user not moving the mouse) would otherwise skew a plain average, so this looks at
/// the smallest cluster of intervals instead, which approximates the device's real report rate
/// during active use.
///
/// Must be Start/Stop-gated (a live hidden window + message pump isn't appropriate to run
/// unconditionally) - Start/Stop mirror VBlankJitterService's lifecycle, except everything here
/// runs on the UI thread (HwndSource message-hook callbacks always run on the thread that created
/// the HwndSource), so no locking is needed the way VBlankJitterService's cross-thread jitter list
/// needs it.
/// </summary>
public sealed class InputLatencyService : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIM_TYPEKEYBOARD = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll")]
    private static extern uint GetMessageTime();

    private const int MaxQueueSamples = 1000;
    private const int MaxIntervalSamples = 500;

    private HwndSource? _source;
    private IntPtr _hwnd = IntPtr.Zero;

    private readonly List<double> _queueDelaysMs = new();
    private readonly List<double> _mouseIntervalsMs = new();
    private readonly List<double> _kbIntervalsMs = new();
    private long _lastMouseTick = -1;
    private long _lastKbTick = -1;

    public bool IsRunning { get; private set; }
    private string _statusText = "Not running - press Start.";

    /// <summary>#258: the wall-clock arrival time of the most recent raw-input event, read by
    /// PresentMonitorService.EstimateInputToPresentMs. Null until at least one event has arrived
    /// while running.</summary>
    public DateTime? LastEventUtc { get; private set; }

    public void Start()
    {
        if (IsRunning) return;
        try
        {
            var parameters = new HwndSourceParameters("TMPlusInputLatencyProbe")
            {
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE - a message-only window, never shown/activated
                Width = 0,
                Height = 0,
            };
            _source = new HwndSource(parameters);
            _hwnd = _source.Handle;
            _source.AddHook(WndProc);

            var devices = new[]
            {
                new RAWINPUTDEVICE { usUsagePage = HID_USAGE_PAGE_GENERIC, usUsage = HID_USAGE_GENERIC_MOUSE, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
                new RAWINPUTDEVICE { usUsagePage = HID_USAGE_PAGE_GENERIC, usUsage = HID_USAGE_GENERIC_KEYBOARD, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            };
            if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                _statusText = "Couldn't register for raw input.";
                StopInternal();
                return;
            }

            _queueDelaysMs.Clear();
            _mouseIntervalsMs.Clear();
            _kbIntervalsMs.Clear();
            _lastMouseTick = -1;
            _lastKbTick = -1;
            LastEventUtc = null;
            IsRunning = true;
            _statusText = "Measuring input queue delay...";
        }
        catch (Exception ex)
        {
            _statusText = $"Couldn't start: {ex.Message}";
            StopInternal();
        }
    }

    public void Stop()
    {
        StopInternal();
        _statusText = "Stopped.";
    }

    private void StopInternal()
    {
        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                var remove = new[]
                {
                    new RAWINPUTDEVICE { usUsagePage = HID_USAGE_PAGE_GENERIC, usUsage = HID_USAGE_GENERIC_MOUSE, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                    new RAWINPUTDEVICE { usUsagePage = HID_USAGE_PAGE_GENERIC, usUsage = HID_USAGE_GENERIC_KEYBOARD, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                };
                RegisterRawInputDevices(remove, (uint)remove.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
            catch { /* best-effort */ }
        }
        try
        {
            _source?.RemoveHook(WndProc);
            _source?.Dispose();
        }
        catch { /* best-effort */ }
        _source = null;
        _hwnd = IntPtr.Zero;
        IsRunning = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
        {
            try { ProcessInput(lParam); } catch { /* best-effort per-event */ }
        }
        return IntPtr.Zero;
    }

    private void ProcessInput(IntPtr lParam)
    {
        // #256: queueing delay - both clocks are GetTickCount's own ms-since-boot base, so a plain
        // (int) subtraction is correct including across its ~49.7-day wraparound (two's-complement
        // arithmetic on the unsigned difference yields the right small signed delta either way).
        uint msgTime = GetMessageTime();
        uint nowTicks = unchecked((uint)Environment.TickCount);
        double delayMs = unchecked((int)(nowTicks - msgTime));

        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != size) return;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);

            if (delayMs is >= 0 and < 2000)
            {
                _queueDelaysMs.Add(delayMs);
                if (_queueDelaysMs.Count > MaxQueueSamples) _queueDelaysMs.RemoveAt(0);
            }
            LastEventUtc = DateTime.UtcNow;

            long nowMs = Environment.TickCount64;
            if (header.dwType == RIM_TYPEMOUSE)
            {
                RecordInterval(_mouseIntervalsMs, ref _lastMouseTick, nowMs);
            }
            else if (header.dwType == RIM_TYPEKEYBOARD)
            {
                RecordInterval(_kbIntervalsMs, ref _lastKbTick, nowMs);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void RecordInterval(List<double> list, ref long lastTick, long nowMs)
    {
        if (lastTick >= 0)
        {
            double interval = nowMs - lastTick;
            if (interval is > 0 and < 1000)
            {
                list.Add(interval);
                if (list.Count > MaxIntervalSamples) list.RemoveAt(0);
            }
        }
        lastTick = nowMs;
    }

    /// <summary>#256/#257: current snapshot - cheap to call every light tick regardless of
    /// IsRunning (registry reads are fast; the in-memory lists are just percentile math over a
    /// bounded sample count).</summary>
    public InputLatencySnapshot GetSnapshot()
    {
        var (mouseQueue, kbQueue) = ReadInputQueueSizes();

        if (_queueDelaysMs.Count == 0)
        {
            return new InputLatencySnapshot
            {
                MouseQueueSizeText = mouseQueue,
                KeyboardQueueSizeText = kbQueue,
                StatusText = _statusText,
            };
        }

        var sorted = new List<double>(_queueDelaysMs);
        sorted.Sort();
        double P(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

        return new InputLatencySnapshot
        {
            SampleCount = sorted.Count,
            P99DelayMs = P(0.99),
            MaxDelayMs = sorted[^1],
            MouseReportHz = ComputeHz(_mouseIntervalsMs),
            KeyboardReportHz = ComputeHz(_kbIntervalsMs),
            MouseQueueSizeText = mouseQueue,
            KeyboardQueueSizeText = kbQueue,
            StatusText = $"{sorted.Count} input event(s) captured. {_statusText}",
        };
    }

    /// <summary>#257: 1000 / the average of the tightest ~10% of observed intervals - approximates
    /// the device's real back-to-back report rate without a genuinely idle gap (the user not
    /// moving the mouse) dragging a plain average down toward an implausibly low "rate".</summary>
    private static double? ComputeHz(List<double> intervals)
    {
        if (intervals.Count < 5) return null;
        var sorted = intervals.OrderBy(x => x).ToList();
        int n = Math.Max(1, sorted.Count / 10);
        double avgSmallest = sorted.Take(n).Average();
        return avgSmallest > 0 ? 1000.0 / avgSmallest : null;
    }

    /// <summary>#257: HKLM\SYSTEM\CurrentControlSet\Services\mouclass\Parameters\MouseDataQueueSize
    /// and the kbdclass\Parameters\KeyboardDataQueueSize equivalent - both default to 100 entries
    /// when absent (Windows' documented default), shown as Unknown/default rather than guessing
    /// the numeric default is actually in effect.</summary>
    public static (string Mouse, string Keyboard) ReadInputQueueSizes()
    {
        string mouse = ReadDword(@"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize");
        string kb = ReadDword(@"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize");
        return (mouse, kb);
    }

    private static string ReadDword(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) is int i ? i.ToString() : "Unknown (using Windows' default, typically 100)";
        }
        catch
        {
            return "Unknown";
        }
    }

    public void Dispose() => StopInternal();
}
