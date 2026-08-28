namespace TaskManagerPlus.Models;

/// <summary>Round 15, #849/#850: one row in the (now trust-aware) "Loaded modules" DataGrid for a
/// selected process - see ModuleTrustInspectionService for how each field is computed.</summary>
public sealed class ProcessModuleInfo
{
    public string ModuleName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;

    /// <summary>#849: "Signed"/"Unsigned"/"Unknown" - see SignatureCheckService.GetStatus.</summary>
    public string SignatureStatus { get; init; } = "Unknown";

    /// <summary>#849: signing certificate's subject CN (falling back to issuer CN, then "Unknown") -
    /// see SignatureCheckService.GetSignerInfo.</summary>
    public string Publisher { get; init; } = "Unknown";

    /// <summary>#849: true when FilePath is under Temp/AppData/LocalAppData/ProgramData/Public/the
    /// user profile root - see WritablePathHeuristics. "Quick flag, not a verdict" - plenty of
    /// legitimate software loads DLLs from AppData (many auto-updaters, Electron apps, ...).</summary>
    public bool IsUserWritableLocation { get; init; }

    /// <summary>#850: true when a file with this exact name also exists directly under
    /// %SystemRoot%\System32, but this module was actually loaded from somewhere else - the classic
    /// DLL side-loading tell for a curated list of well-known target DLL names.</summary>
    public bool IsSideLoadSuspect { get; init; }

    /// <summary>#850: the System32 counterpart path this module's name collides with, when
    /// IsSideLoadSuspect is true - shown side by side with FilePath (the actually-loaded path) in the
    /// UI. Null when IsSideLoadSuspect is false.</summary>
    public string? System32CounterpartPath { get; init; }
}
