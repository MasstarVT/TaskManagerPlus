using System.Collections.ObjectModel;

namespace TaskManagerPlus.Models;

/// <summary>
/// One node in the Processes tab's optional tree view (Round 7 #1) - wraps an existing ProcessRow
/// (no duplicated state) plus its resolved children, built fresh from the flat Processes collection
/// each refresh tick by ProcessesViewModel.BuildProcessTree. A process whose parent isn't currently
/// running (or was never resolved) becomes a root node, the same "orphan" treatment Task Manager's
/// own Details-tab tree uses.
/// </summary>
public sealed class ProcessTreeNode
{
    public ProcessRow Row { get; }
    public ObservableCollection<ProcessTreeNode> Children { get; } = new();

    public ProcessTreeNode(ProcessRow row)
    {
        Row = row;
    }
}
