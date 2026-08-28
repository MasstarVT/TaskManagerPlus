using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// #406: pinned leak-watch list - a small, independent set of processes (by image name, so the
/// watch survives that process restarting and survives this app restarting too - see
/// LeakWatchSettingsService) sampled at a fixed 5s interval regardless of whatever poll interval
/// the Processes tab itself is configured for, with a longer history window than the sparkline
/// column (#401) keeps. Deliberately its own tiny sampler rather than piggybacking on
/// ProcessesViewModel's timer/history - a pinned watch is meant to keep working exactly the same
/// whether the user has the Processes tab's poll interval set to 0.5s or 10s.
///
/// Toggled from the Processes tab's right-click "Watch for leaks" menu item (ProcessesViewModel.
/// ToggleLeakWatchCommand) and rendered as one glow+gradient chart per watched process on the
/// Memory tab (MemoryView's "Leak watch list" panel) - reuses PerformanceViewModel.LineOf for
/// the chart pair, same visual recipe as every other history chart in this app.
/// </summary>
public sealed class LeakWatchViewModel : ObservableObject, IDisposable
{
    private const int MaxWatched = 8;
    private const int HistoryLength = 240; // 240 samples @ 5s = 20 minutes
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

    private readonly LeakWatchSettings _settings;
    private readonly DispatcherTimer _timer;

    public ObservableCollection<WatchedProcessViewModel> WatchedProcesses { get; } = new();

    public Axis[] HiddenXAxes { get; }
    public Axis[] MemoryBytesYAxes { get; }

    public LeakWatchViewModel()
    {
        _settings = LeakWatchSettingsService.Load();

        HiddenXAxes = new[] { new Axis { IsVisible = false, MinLimit = 0, MaxLimit = HistoryLength - 1, ShowSeparatorLines = false } };
        MemoryBytesYAxes = new[]
        {
            new Axis
            {
                MinLimit = 0,
                Labeler = v => Formatting.FormatBytes(v),
                LabelsPaint = new SolidColorPaint(new SKColor(0x9A, 0x9A, 0xA2)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(0x33, 0x33, 0x3A, 160)) { StrokeThickness = 1 },
            },
        };

        foreach (var name in _settings.WatchedImageNames.Distinct(StringComparer.OrdinalIgnoreCase))
            WatchedProcesses.Add(new WatchedProcessViewModel(name, HistoryLength));

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SampleInterval };
        _timer.Tick += async (_, _) => await SampleAsync();
        _timer.Start();

        _ = SampleAsync();
    }

    public bool IsWatched(string imageName) =>
        WatchedProcesses.Any(w => w.ImageName.Equals(imageName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds or removes <paramref name="imageName"/> from the watch list and persists the
    /// change immediately. A no-op (returns false) once MaxWatched is already reached and the
    /// name isn't already watched - a handful of live charts is plenty before the panel becomes
    /// unusable; the user can unwatch one to make room.</summary>
    public bool Toggle(string imageName)
    {
        var existing = WatchedProcesses.FirstOrDefault(w => w.ImageName.Equals(imageName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            WatchedProcesses.Remove(existing);
            Persist();
            return true;
        }

        if (WatchedProcesses.Count >= MaxWatched) return false;

        var added = new WatchedProcessViewModel(imageName, HistoryLength);
        WatchedProcesses.Add(added);
        Persist();
        _ = SampleOneAsync(added);
        return true;
    }

    private void Persist()
    {
        _settings.WatchedImageNames = WatchedProcesses.Select(w => w.ImageName).ToList();
        LeakWatchSettingsService.Save(_settings);
    }

    private async Task SampleAsync()
    {
        if (WatchedProcesses.Count == 0) return;
        foreach (var watched in WatchedProcesses.ToList())
            await SampleOneAsync(watched);
    }

    private static async Task SampleOneAsync(WatchedProcessViewModel watched)
    {
        var (privateBytes, handleCount) = await Task.Run(() => SampleByName(watched.ImageName));
        watched.PushSample(privateBytes, handleCount);
    }

    /// <summary>Sums PrivateMemorySize64/HandleCount across every currently-running process with
    /// this image name (several instances sharing a name are combined into one trend, the same
    /// aggregation ProcessHistoryService uses for the persistent per-name history) - null when
    /// nothing by that name is currently running.</summary>
    private static (long? PrivateBytes, int HandleCount) SampleByName(string imageName)
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName(imageName); }
        catch { return (null, 0); }

        if (procs.Length == 0) return (null, 0);

        long total = 0;
        int handles = 0;
        foreach (var proc in procs)
        {
            try { total += proc.PrivateMemorySize64; } catch { /* exited mid-read or access denied */ }
            try { handles += proc.HandleCount; } catch { /* ignore */ }
            proc.Dispose();
        }
        return (total, handles);
    }

    public void Dispose() => _timer.Stop();
}

/// <summary>One pinned-watch chart's worth of state - a rolling PrivateBytes history sampled at
/// LeakWatchViewModel's fixed 5s cadence, rendered as a glow+gradient LineSeries pair.</summary>
public sealed class WatchedProcessViewModel : ObservableObject
{
    private readonly int _historyLength;

    public string ImageName { get; }
    public ObservableCollection<double> MemoryHistory { get; }
    public ISeries[] Series { get; }

    private string _latestValueText = "Waiting for a sample…";
    public string LatestValueText { get => _latestValueText; private set => SetProperty(ref _latestValueText, value); }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }

    public WatchedProcessViewModel(string imageName, int historyLength)
    {
        ImageName = imageName;
        _historyLength = historyLength;
        MemoryHistory = new ObservableCollection<double>(Enumerable.Repeat(0.0, historyLength));

        var (glow, core) = PerformanceViewModel.LineOf(MemoryHistory, SKColors.MediumPurple);
        Series = new ISeries[] { glow, core };
    }

    public void PushSample(long? privateBytes, int handleCount)
    {
        if (privateBytes is { } bytes)
        {
            IsRunning = true;
            MemoryHistory.Add(bytes);
            while (MemoryHistory.Count > _historyLength) MemoryHistory.RemoveAt(0);
            LatestValueText = $"{Formatting.FormatBytes(bytes)} private bytes · {handleCount:N0} handles";
        }
        else
        {
            IsRunning = false;
            LatestValueText = "Not currently running";
        }
    }
}
