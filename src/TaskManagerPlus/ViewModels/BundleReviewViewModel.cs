using System.Collections.ObjectModel;
using Microsoft.Win32;
using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// suggestions.md #995: "Bundle review mode" - backs the Troubleshoot tab's "Open bundle" sub-page.
/// Loads a previously exported evidence bundle .zip (from the earlier Evidence Bundle chunk, see
/// BundleReviewService's remarks on its exact folder shape) into a dedicated read-only summary
/// view: findings, timeline, and any saved baselines the source machine had. A persistent banner
/// (SourceMachineName/CapturedAtText/BannerText) states which machine and when throughout.
///
/// Scope note (explicit, since #995's task text allows this): this is a NEW dedicated read-only
/// summary view bound to the loaded static data, not a "dual-mode live/static" retrofit of the
/// existing Summary/Timeline/Baselines tabs - those still only ever show this machine's live data.
/// Wiring every existing tab to optionally read from a loaded bundle would be a much larger
/// refactor (each already assumes a live ViewModel behind it); a clean, real "load and view" flow
/// for findings/timeline/specs/baselines is the bar this implementation targets.
/// </summary>
public sealed class BundleReviewViewModel : ObservableObject
{
    private string? _extractedDirectory;

    public ObservableCollection<HealthIssue> Findings { get; } = new();
    public ObservableCollection<TimelineEvent> TimelineEvents { get; } = new();
    public ObservableCollection<PerformanceBaseline> Baselines { get; } = new();
    public ObservableCollection<EvidenceBundleManifestEntry> Files { get; } = new();

    private bool _isBundleLoaded;
    public bool IsBundleLoaded { get => _isBundleLoaded; private set => SetProperty(ref _isBundleLoaded, value); }

    private string _bannerText = string.Empty;
    /// <summary>The persistent banner text - "Reviewing a bundle from &lt;machine&gt;, captured
    /// &lt;time&gt;" - shown throughout this sub-page while a bundle is loaded.</summary>
    public string BannerText { get => _bannerText; private set => SetProperty(ref _bannerText, value); }

    private string _statusText = "No bundle loaded yet - use \"Open bundle...\" to review one someone sent you.";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public RelayCommand OpenBundleCommand { get; }
    public RelayCommand CloseBundleCommand { get; }

    public BundleReviewViewModel()
    {
        OpenBundleCommand = new RelayCommand(_ => OpenBundle());
        CloseBundleCommand = new RelayCommand(_ => CloseBundle(), _ => IsBundleLoaded);
    }

    private void OpenBundle()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open an evidence bundle",
            Filter = "Evidence bundle (*.zip)|*.zip|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            CloseBundle(); // release any previously loaded bundle's temp folder first

            var loaded = BundleReviewService.Extract(dialog.FileName);
            _extractedDirectory = loaded.ExtractedDirectory;

            Findings.Clear();
            foreach (var f in loaded.Findings) Findings.Add(f);
            TimelineEvents.Clear();
            foreach (var e in loaded.TimelineEvents) TimelineEvents.Add(e);
            Baselines.Clear();
            foreach (var b in loaded.Baselines) Baselines.Add(b);
            Files.Clear();
            foreach (var entry in loaded.Manifest.Entries) Files.Add(entry);

            var generatedLocal = loaded.Manifest.GeneratedAtUtc.ToLocalTime();
            string machine = string.IsNullOrWhiteSpace(loaded.Manifest.MachineName) ? "(unknown machine)" : loaded.Manifest.MachineName;
            BannerText = $"Reviewing a bundle from {machine} - captured {generatedLocal:g}" +
                (loaded.Manifest.WasScrubbed ? " (personal info was scrubbed before export)" : string.Empty);
            StatusText = $"Loaded {Findings.Count} finding(s), {TimelineEvents.Count} timeline event(s), {Baselines.Count} baseline(s), {Files.Count} file(s) from \"{System.IO.Path.GetFileName(dialog.FileName)}\".";
            IsBundleLoaded = true;
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't open that bundle: {ex.Message}";
        }
    }

    private void CloseBundle()
    {
        BundleReviewService.Cleanup(_extractedDirectory);
        _extractedDirectory = null;
        Findings.Clear();
        TimelineEvents.Clear();
        Baselines.Clear();
        Files.Clear();
        BannerText = string.Empty;
        IsBundleLoaded = false;
        StatusText = "No bundle loaded yet - use \"Open bundle...\" to review one someone sent you.";
    }
}
