namespace TaskManagerPlus.Models;

/// <summary>#664: one observed change of the active power-scheme GUID between polls - persisted to
/// power-plan-history.json (PowerPlanHistoryService) so "my settings keep reverting" has a real
/// timestamped trail, since vendor utilities and games are known to silently call
/// `powercfg /setactive` behind the user's back.</summary>
public sealed class PowerPlanChangeEvent
{
    public DateTime Timestamp { get; init; }
    public string FromPlanName { get; init; } = string.Empty;
    public string ToPlanName { get; init; } = string.Empty;
}
