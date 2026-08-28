namespace TaskManagerPlus.Models;

/// <summary>
/// Rolling variance stats for one AIO/pump fan channel (#616) - matched by name
/// ("Pump"/"AIO"/"W_PUMP") and held to a different rule than case fans: a pump should run at a
/// near-constant RPM, so *variance* rather than absolute RPM is the fault signal. Computed
/// on-the-fly by EnergyThermalsViewModel from a short rolling sample window; not persisted.
/// </summary>
public sealed class PumpStatus
{
    public string Name { get; init; } = string.Empty;
    public double CurrentRpm { get; init; }
    public double MeanRpm { get; init; }
    public double StdDevRpm { get; init; }

    /// <summary>StdDevRpm / MeanRpm - the fault signal for a pump (unlike a case fan, whose
    /// absolute RPM matters more than its steadiness).</summary>
    public double CoefficientOfVariation { get; init; }

    public bool IsVariable { get; init; }
}
