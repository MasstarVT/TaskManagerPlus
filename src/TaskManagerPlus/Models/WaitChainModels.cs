namespace TaskManagerPlus.Models;

/// <summary>Item 64: one thread's wait chain, as returned by the Wait Chain Traversal API
/// (WaitChainAnalysisService.Analyze) - a plain, pre-formatted list of node descriptions (rendered
/// as an indented list in the UI, matching Resource Monitor's own "Analyze Wait Chain" display)
/// rather than a structured node type, since every node is shown, not filtered/sorted/re-grouped -
/// there's no need for the UI layer to know each node's raw ObjectType/ObjectStatus.</summary>
public sealed class WaitChainThreadResult
{
    public int ThreadId { get; init; }

    /// <summary>True when GetThreadWaitChain itself flagged this chain as a cycle - a genuine
    /// deadlock (thread A waits on a lock held by thread B, which is itself somewhere in the same
    /// chain), not just an ordinary, resolvable wait.</summary>
    public bool IsDeadlockCycle { get; init; }

    public List<string> Nodes { get; init; } = new();
}

/// <summary>Item 64: result of analysing every thread of one process - one WaitChainThreadResult
/// per thread the API could resolve a chain for (a thread not currently waiting on anything the
/// API tracks simply contributes nothing, not an error). ErrorMessage is set only when the whole
/// analysis couldn't run at all (API unavailable, process already exited, access denied) or found
/// nothing at all across every thread.</summary>
public sealed class WaitChainResult
{
    public List<WaitChainThreadResult> Chains { get; init; } = new();
    public string? ErrorMessage { get; set; }
}
