namespace TaskManagerPlus.Models;

/// <summary>
/// #631: parsed processor-power-management settings for the active `powercfg` scheme, read from
/// `powercfg /qh` (query, including hidden settings) text output - minimum/maximum processor
/// state and core-parking minimum cores, AC and DC. `powercfg /qh`'s text layout is exactly as
/// undocumented/unversioned as every other powercfg text-parse in this app (see
/// PowerPlanService.ExtractSettingPercent's remarks) - any field not found in the output is left
/// null ("Unknown") rather than guessed.
/// </summary>
public sealed class ProcessorPowerSettings
{
    public int? MinProcessorStateAcPercent { get; init; }
    public int? MaxProcessorStateAcPercent { get; init; }
    public int? MinProcessorStateDcPercent { get; init; }
    public int? MaxProcessorStateDcPercent { get; init; }
    public int? CoreParkingMinCoresAcPercent { get; init; }
    public int? CoreParkingMinCoresDcPercent { get; init; }
}
