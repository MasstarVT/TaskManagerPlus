namespace TaskManagerPlus.Models;

/// <summary>Round 14, item 25: crash-analysis settings persisted to
/// %AppData%\TaskManagerPlus\crash-analysis.json (via AppPaths, same shape as every other
/// settings file in this app, e.g. PollIntervalSettings) - just the symbol cache folder
/// !analyze -v needs; the symbol path itself is derived from this folder via
/// SymbolServerService.SuggestedPathTemplate rather than stored redundantly.</summary>
public sealed class CrashAnalysisSettings
{
    public string SymbolCacheFolder { get; set; } = @"C:\Symbols";

    public static CrashAnalysisSettings Defaults => new();
}
