using Helpdesk.Shared.Models;

namespace Helpdesk.Application.RequestTasks;

public static class RequestHoldPolicy
{
    public const string OperatorStatus = "OnHold";
    public const string OperatorReason = "Request paused by an operator.";

    public static bool OwnsReconciliationHold(Request request) => request.WorkflowStatus == "NeedsReconciliation"
        && request.WorkflowBlockReason == AutomationTaskStatuses.ReconciliationHoldReason;

    public static bool OwnsOperatorMetadata(Request request) => request.WorkflowStatus == OperatorStatus
        && request.WorkflowBlockReason == OperatorReason;

    public static bool HasUnrelatedWorkflowHold(Request request) => request.WorkflowStatus is "Blocked" or "Failed" or "Cancelled"
        || !string.IsNullOrWhiteSpace(request.WorkflowBlockReason)
            && !OwnsReconciliationHold(request) && !OwnsOperatorMetadata(request);

    public static bool IsOperatorHeld(Request request) => request.OperatorHold == true
        || request.OperatorHold is null && request.State == TicketState.OnHold
            && !OwnsReconciliationHold(request) && !HasUnrelatedWorkflowHold(request);

    public static bool BlocksStart(Request request) => IsOperatorHeld(request) || HasUnrelatedWorkflowHold(request)
        || request.WorkflowStatus == "NeedsReconciliation";
}
