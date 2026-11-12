namespace HelpDesk.NewWeb.Models;

/// <summary>Display copy for request progress; provider and local state remain separate.</summary>
public static class RequestProgressLabels
{
    public static string Readable(string? value) => value switch
    {
        "InProgress" => "In progress",
        "InSync" => "In sync",
        "OnHold" => "On hold",
        "NeedsReconciliation" => "Needs reconciliation",
        "PendingApproval" => "Pending approval",
        "WaitingReply" => "Waiting for reply",
        "ImportPending" => "Import pending",
        _ => WorkspaceLabels.Readable(value)
    };
}
