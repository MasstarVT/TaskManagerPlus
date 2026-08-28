using TaskManagerPlus.Common;
using TaskManagerPlus.Models;
using TaskManagerPlus.Services;

namespace TaskManagerPlus.ViewModels;

/// <summary>
/// Round 14, items 17/18/23/24: one row in the Stability tab's "Dump analysis" list - wraps the
/// immutable ParsedDumpInfo (MinidumpParserService's binary-parse result) with the mutable,
/// per-row UI state (analysis cache, in-progress flag, commands) that a plain init-only model
/// can't carry itself. Also precomputes a handful of display strings so the XAML doesn't need a
/// pile of new single-purpose converters for this one card.
/// </summary>
public sealed class DumpRowViewModel : ObservableObject
{
    public ParsedDumpInfo Parsed { get; }

    /// <summary>Round 15, items 28-37: the fully decoded bugcheck (labelled parameters, guidance,
    /// per-code sub-lines) for this dump's own binary-parsed BugcheckCode/BugcheckParameters -
    /// built off the UI thread by StabilityViewModel.BuildDumpAnalysisBundle (item #35's pool-tag
    /// resolution can involve a bounded driver-binary scan) and passed in here rather than
    /// computed in this constructor, which callers run on the UI thread.</summary>
    public BugcheckDecodedInfo Decoded { get; }

    private CdbAnalysisResult? _analysis;
    public CdbAnalysisResult? Analysis { get => _analysis; private set => SetProperty(ref _analysis, value); }

    private bool _isAnalyzing;
    public bool IsAnalyzing { get => _isAnalyzing; private set => SetProperty(ref _isAnalyzing, value); }

    private string? _analyzeError;
    public string? AnalyzeError { get => _analyzeError; private set => SetProperty(ref _analyzeError, value); }

    public bool CanUseDebugger { get; }
    public string DebuggerHintText { get; }

    public string ContentsText => Parsed.StreamKinds.Count > 0 ? string.Join(", ", Parsed.StreamKinds) : "(no stream directory - classic kernel/complete dump format)";
    public string ModuleCountText => Parsed.Modules.Count == 1 ? "1 module" : $"{Parsed.Modules.Count} modules";

    /// <summary>Item 15: whichever of IncompleteReason (a header-level truncation/corruption
    /// check failed) or ParseError (a partial stream-directory-walk failure that didn't trip the
    /// header-level check, or an outright unreadable/unrecognized file) applies - the two are
    /// mutually exclusive on ParsedDumpInfo, so this just picks whichever one is set.</summary>
    public string? IssueText => Parsed.IncompleteReason ?? Parsed.ParseError;

    public AsyncRelayCommand AnalyzeCommand { get; }
    public RelayCommand OpenInWinDbgCommand { get; }

    public DumpRowViewModel(ParsedDumpInfo parsed, DebuggerAvailability debugger, BugcheckDecodedInfo decoded)
    {
        Parsed = parsed;
        Decoded = decoded;
        CanUseDebugger = debugger.AnyFound;
        DebuggerHintText = debugger.AnyFound
            ? string.Empty
            : "No debugger found - install the \"Debugging Tools for Windows\" feature from the Windows SDK, or WinDbg Preview from the Microsoft Store, to enable analysis.";

        AnalyzeCommand = new AsyncRelayCommand(async () =>
        {
            if (debugger.CdbPath is null) { AnalyzeError = "cdb.exe not found."; return; }
            IsAnalyzing = true;
            AnalyzeError = null;
            try
            {
                var result = await DebuggerToolsService.RunAnalyzeAsync(debugger.CdbPath, Parsed.FilePath);
                if (result.Error is not null) AnalyzeError = result.Error;
                Analysis = result;
            }
            catch (Exception ex)
            {
                AnalyzeError = $"Analysis failed: {ex.Message}";
            }
            finally
            {
                IsAnalyzing = false;
            }
        }, () => debugger.CdbPath is not null);

        OpenInWinDbgCommand = new RelayCommand(
            () => DebuggerToolsService.TryOpenInWinDbg(debugger, Parsed.FilePath),
            () => debugger.AnyFound);
    }
}
