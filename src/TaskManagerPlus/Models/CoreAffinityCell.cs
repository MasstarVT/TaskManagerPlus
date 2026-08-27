namespace TaskManagerPlus.Models;

/// <summary>One core's worth of the CPU tab's core-affinity heatmap (Round 8 #24) - which of the
/// current top-CPU processes have a thread whose *preferred* (ideal) processor is this core. See
/// Services/CoreAffinityService for why "ideal processor" rather than "actually running on right
/// now" is the honest framing here.</summary>
public sealed class CoreAffinityCell
{
    public int CoreIndex { get; init; }
    public int ProcessCount { get; init; }
    public string ProcessNames { get; init; } = string.Empty;
}
