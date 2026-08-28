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

    private CdbAnalysisResult? _analysis;
    public CdbAnalysisResult? Analysis { get => _analysis; private set => SetProperty(ref _analysis, value); }

    private bool _isAnalyzing;
    public bool IsAnalyzing { get => _isAnalyzing; private set => SetProperty(ref _isAnalyzing, value); }

    private string? _analyzeError;
    public string? AnalyzeError { get => _analyzeError; private set => SetProperty(ref _analyzeError, value); }

    public bool CanUseDebugger { get; }
    public string DebuggerHintText { get; }

    public string ContentsText => Parsed.StreamKinds.Count > 0 ? string.Join(", ", Parsed.StreamKinds) : "(no stream directory - classic kernel/complete dump format)";
    public string ParametersText => Parsed.BugcheckParameters.Length > 0 ? string.Join(", ", Parsed.BugcheckParameters) : "(none read)";
    public string ModuleCountText => Parsed.Modules.Count == 1 ? "1 module" : $"{Parsed.Modules.Count} modules";

    /// <summary>Item 15: whichever of IncompleteReason (a header-level truncation/corruption
    /// check failed) or ParseError (a partial stream-directory-walk failure that didn't trip the
    /// header-level check, or an outright unreadable/unrecognized file) applies - the two are
    /// mutually exclusive on ParsedDumpInfo, so this just picks whichever one is set.</summary>
    public string? IssueText => Parsed.IncompleteReason ?? Parsed.ParseError;

    public AsyncRelayCommand AnalyzeCommand { get; }
    public RelayCommand OpenInWinDbgCommand { get; }

    public DumpRowViewModel(ParsedDumpInfo parsed, DebuggerAvailability debugger)
    {
        Parsed = parsed;
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
