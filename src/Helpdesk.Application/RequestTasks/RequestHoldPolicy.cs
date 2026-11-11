using Helpdesk.Shared.Models;

namespace Helpdesk.Application.RequestTasks;

public static class RequestHoldPolicy
{
    public const string OperatorStatus = "OnHold";
    public const string OperatorReason = "Request paused by an operator.";

    public static bool OwnsReconciliationHold(Request request) => IsStatus(request, "NeedsReconciliation")
        && request.WorkflowBlockReason == AutomationTaskStatuses.ReconciliationHoldReason;

    public static bool OwnsOperatorMetadata(Request request) => IsStatus(request, OperatorStatus)
        && request.WorkflowBlockReason == OperatorReason;

    public static bool HasUnrelatedWorkflowHold(Request request) => IsStatus(request, "Blocked")
        || IsStatus(request, "Failed") || IsStatus(request, "Cancelled")
        || !string.IsNullOrWhiteSpace(request.WorkflowBlockReason)
            && !OwnsReconciliationHold(request) && !OwnsOperatorMetadata(request);

    public static bool IsOperatorHeld(Request request) => request.OperatorHold == true
        || request.OperatorHold is null && request.State == TicketState.OnHold
            && !OwnsReconciliationHold(request) && !HasUnrelatedWorkflowHold(request);

    public static bool BlocksStart(Request request) => IsOperatorHeld(request) || HasUnrelatedWorkflowHold(request)
        || IsStatus(request, "NeedsReconciliation") || IsStatus(request, OperatorStatus);

    private static bool IsStatus(Request request, string status)
        => string.Equals(request.WorkflowStatus, status, StringComparison.OrdinalIgnoreCase);
}
