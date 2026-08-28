namespace TaskManagerPlus.Models;

/// <summary>#964: how a fired alert (a rule-engine finding, or one of SummaryViewModel's three
/// fixed threshold alerts) should be delivered - default Toast, matching every rule pack/behavior
/// that existed before #964. SilentLogOnly still gets appended to alerts-history.jsonl (#963), it
/// just never pops a toast/balloon - unless #965's escalation forces it through.</summary>
public enum AlertChannel
{
    Toast,
    TrayBalloon,
    SilentLogOnly,
}
