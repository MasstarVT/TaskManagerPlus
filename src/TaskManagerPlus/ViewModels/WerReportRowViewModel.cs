using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Round 16, items 38-49: one row in the Stability tab's "Error reports" card - wraps the
/// immutable WerReport (WerReportService's parsed Report.wer record) with the mutable, per-row UI
/// state (item 49's multi-select, item 45's copy/search actions) a plain init-only model can't
/// carry itself - the same shape DumpRowViewModel already uses for ParsedDumpInfo.
/// </summary>
public sealed class WerReportRowViewModel : ObservableObject
{
    public WerReport Report { get; }

    private bool _isSelected;

    /// <summary>Item 49: multi-select state for the later support-bundle export chunk (#100) -
    /// this chunk only wires the selection state itself, not the export.</summary>
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    private string? _actionStatusText;
    public string? ActionStatusText { get => _actionStatusText; private set => SetProperty(ref _actionStatusText, value); }

    public RelayCommand CopySignatureCommand { get; }
    public RelayCommand OpenWebSearchCommand { get; }

    public WerReportRowViewModel(WerReport report)
    {
        Report = report;

        CopySignatureCommand = new RelayCommand(() =>
        {
            try
            {
                System.Windows.Clipboard.SetText(WerReportService.BuildCrashSignatureText(report));
                ActionStatusText = "Copied to clipboard.";
            }
            catch (Exception ex)
            {
                ActionStatusText = $"Couldn't copy: {ex.Message}";
            }
        });

        OpenWebSearchCommand = new RelayCommand(() =>
        {
            try
            {
                string query = Uri.EscapeDataString(
                    $"{report.AppName} {report.ModName} {report.ExceptionCode} {(report.IsHang ? "hang" : "crash")}".Trim());
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://www.bing.com/search?q={query}")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                ActionStatusText = $"Couldn't open browser: {ex.Message}";
            }
        });
    }
}

/// <summary>Item 39: display wrapper around WerReportService.GroupByBucket's plain WerBucketGroup
/// - swaps its List&lt;WerReport&gt; for the row view models (with selection/commands) the "Error
/// reports" card's bucket expanders actually bind against. Immutable after construction (built
/// fresh on every refresh, same as WerBucketGroup itself), so this plain class doesn't need
/// INotifyPropertyChanged.</summary>
public sealed class WerBucketRowViewModel
{
    public string BucketKey { get; }
    public bool HasRealBucketId { get; }
    public string BucketKindText { get; }
    public string AppName { get; }
    public string ModName { get; }
    public int Count { get; }
    public DateTime LastSeen { get; }
    public List<WerReportRowViewModel> Reports { get; }

    public WerBucketRowViewModel(WerBucketGroup group, List<WerReportRowViewModel> reports)
    {
        BucketKey = group.BucketKey;
        HasRealBucketId = group.HasRealBucketId;
        BucketKindText = group.HasRealBucketId ? "WER bucket ID" : "Derived signature (no WER bucket ID present)";
        AppName = group.AppName;
        ModName = group.ModName;
        Count = group.Count;
        LastSeen = group.LastSeen;
        Reports = reports;
    }
}
