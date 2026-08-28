namespace TaskManagerPlus.Models;

/// <summary>#978: one remediation action this app ran that declared RequiresReboot - backs the
/// "restart pending — including from N fix(es) you ran" banner. See
/// Services.PendingRebootActionsService for the persistence/clearing story.</summary>
public sealed class PendingRebootAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ActionTitle { get; set; } = string.Empty;
    public DateTime RanAtUtc { get; set; } = DateTime.UtcNow;
}
