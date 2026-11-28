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

    public static bool BlocksStart(Request request) => request.AdministrativeClosureReason != null || IsOperatorHeld(request) || HasUnrelatedWorkflowHold(request)
        || IsStatus(request, "NeedsReconciliation") || IsStatus(request, OperatorStatus);

    internal static bool IsFailureState(Request request) => IsStatus(request, "Blocked") || IsStatus(request, "Failed");

    internal static bool OwnsFailureHold(Request request, RequestTask task) =>
        IsFailureState(request) && request.WorkflowFailureTaskId == task.Id
        && request.WorkflowFailureAttemptId == task.AutomationAttemptCorrelationId
        && request.WorkflowFailureCategory == FailureCategory(task.LastAutomationStatus);

    internal static RequestFailureCategory FailureCategory(string? status) => status switch
    {
        AutomationTaskStatuses.SubmitRejectedManualRetry => RequestFailureCategory.SubmissionRejected,
        AutomationTaskStatuses.SubmitFailedManualRetry => RequestFailureCategory.PreDispatchFailure,
        _ => RequestFailureCategory.TaskFailed
    };

    internal static void SetFailureHold(Request request, RequestTask task, string status, string reason, DateTimeOffset now)
    {
        request.OperatorHold ??= IsOperatorHeld(request);
        request.WorkflowFailureTaskId = task.Id;
        request.WorkflowFailureAttemptId = task.AutomationAttemptCorrelationId;
        request.WorkflowFailureCategory = FailureCategory(task.LastAutomationStatus);
        request.WorkflowStatus = status;
        request.WorkflowBlockReason = reason;
        request.State = TicketState.OnHold;
        request.WorkflowUpdatedAt = now;
        request.UpdatedAt = now.UtcDateTime;
        request.ClosedAt = null;
    }

    internal static void ClearFailureHold(Request request)
    {
        request.OperatorHold ??= IsOperatorHeld(request);
        request.WorkflowFailureTaskId = null;
        request.WorkflowFailureAttemptId = null;
        request.WorkflowFailureCategory = null;
        request.WorkflowStatus = null;
        request.WorkflowBlockReason = null;
    }

    private static bool IsStatus(Request request, string status)
        => string.Equals(request.WorkflowStatus, status, StringComparison.OrdinalIgnoreCase);
}
