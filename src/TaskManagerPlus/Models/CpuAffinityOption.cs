using TaskManagerPlus.Common;

namespace TaskManagerPlus.Models;

/// <summary>One logical-processor checkbox in the Processes tab's on-demand CPU affinity editor
/// (Round 7 #5) - loaded from the selected process's current affinity mask, only applied back via
/// an explicit "Apply affinity" button (never live-edited), the same "expensive/impactful, so make
/// it explicit" tradeoff the modules/environment viewers already use.</summary>
public sealed class CpuAffinityOption : ObservableObject
{
    public int Index { get; init; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}
