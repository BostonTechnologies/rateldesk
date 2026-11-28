using System.Text.Json;
using System.Text.Json.Nodes;
using Helpdesk.Application.Workflow;
using Helpdesk.Shared.DTOs.Orchestration;
using Helpdesk.Shared.Models;

namespace Helpdesk.Application.RequestTasks;

public static class RequestTaskRetryRecovery
{
    internal const string AttemptPrefix = "automation-attempt-";
    private const string AmbiguousHold = "The request has a failure hold that cannot be attributed to this attempt. Review the request holds and task history before resuming.";

    public static bool HasPendingRetryIntent(RequestTask task) =>
        task.AutomationContinuationPending && IsUnstartedRetry(task);

    public static bool IsReplacementAttempt(RequestTask task) =>
        task.AutomationAttemptCorrelationId?.StartsWith(AttemptPrefix, StringComparison.Ordinal) == true
        && task.AutomationAttemptHistoryJson is { Length: > 2 };

    public static bool IsUnstartedRetry(RequestTask task) =>
        task.Type == RequestTaskType.Automation && task.Status == RequestTaskStatus.Pending && task.State == TicketState.New
        && task.StartedAt is null && task.CompletedAt is null && task.LastAutomationStatus is null
        && task.AutomationDeadlineUtcTicks is null && task.LastAutomationUpdatedAt is null
        && task.AutomationOriginJson is null && task.AutomationDispatchPayloadJson is null
        && task.OrchestrationExternalRequestId is null && task.OrchestrationExternalRunId is null
        && task.OrchestratorExecutionId is null && task.ProviderDiagnostic is null
        && IsReplacementAttempt(task);

    internal static JsonArray ReadHistory(RequestTask task)
    {
        try
        {
            return string.IsNullOrWhiteSpace(task.AutomationAttemptHistoryJson) ? new JsonArray()
                : JsonNode.Parse(task.AutomationAttemptHistoryJson) as JsonArray ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Automation attempt history is invalid. Reconcile it before retrying.");
        }
    }

    internal static bool CanReleaseHold(Request parent, RequestTask task, IReadOnlyList<RequestTask> tasks, JsonArray history)
    {
        if (!RequestHoldPolicy.IsFailureState(parent)) return false;
        if (parent.WorkflowFailureTaskId is not null || parent.WorkflowFailureCategory is not null
            || parent.WorkflowFailureAttemptId is not null)
        {
            if (task.Status == RequestTaskStatus.Failed && RequestHoldPolicy.OwnsFailureHold(parent, task)) return true;
            if (parent.WorkflowFailureTaskId != task.Id && task.Status == RequestTaskStatus.Failed) return false;
            throw new RequestProgressionHeldException(AmbiguousHold);
        }

        var failed = task.Status == RequestTaskStatus.Failed
            ? new FailureEvidence(task.AutomationAttemptCorrelationId, task.LastAutomationStatus, task.FailureReason,
                task.StartedAt, task.LastAutomationUpdatedAt, task.ProviderDiagnostic, task.OrchestrationExternalRequestId,
                task.OrchestrationExternalRunId, task.OrchestratorExecutionId)
            : ReadPreviousFailure(history);
        if (failed is null || failed.AutomationAttemptCorrelationId is not { Length: > 0 } attempt
            || AutomationTaskStatuses.RequiresManualReconciliation(failed.LastAutomationStatus)
            || failed.RetriedAtUtc is not null && task.AutomationAttemptCorrelationId == attempt
            || failed.TaskId is not null && failed.TaskId != task.Id
            || failed.RequestId is not null && failed.RequestId != task.RequestId
            || failed.Status is not (null or RequestTaskStatus.Failed)
            || (failed.LastAutomationUpdatedAt ?? failed.StartedAt) is not { } failureTime
            || parent.WorkflowUpdatedAt is not { } heldAt || heldAt < failureTime || heldAt > failed.RetriedAtUtc
            || RequestHoldPolicy.IsOperatorHeld(parent) || task.IsBlocked
            || RequestTaskApprovalGate.IsBlockedByEarlierApproval(task, tasks)
            || tasks.Any(sibling => sibling.Id != task.Id && (sibling.Status is RequestTaskStatus.Failed or RequestTaskStatus.Cancelled
                || IsUnstartedRetry(sibling) && !HasPendingRetryIntent(sibling)
                || AutomationTaskStatuses.RequiresManualReconciliation(sibling.LastAutomationStatus)))
            || !MatchesLegacyFailure(parent, task.Name, failed))
            throw new RequestProgressionHeldException(AmbiguousHold);

        return true;
    }

    private static bool MatchesLegacyFailure(Request parent, string name, FailureEvidence failed)
    {
        var namedFailure = parent.WorkflowStatus == "Failed" ? $"Critical task '{name}' failed." : $"Task '{name}' failed.";
        var rejection = failed.LastAutomationStatus == AutomationTaskStatuses.SubmitRejectedManualRetry;
        var hasProviderReferences = failed.OrchestrationExternalRequestId is not null || failed.OrchestrationExternalRunId is not null
            || failed.OrchestratorExecutionId is not null;
        if (rejection && (hasProviderReferences || failed.ProviderDiagnostic is { } diagnostic
            && (diagnostic is not { Stage: "admission", ExecutionCreated: false } || diagnostic.CorrelationId != failed.AutomationAttemptCorrelationId)))
            return false;

        if (parent.WorkflowBlockReason == namedFailure)
            return !string.IsNullOrWhiteSpace(failed.FailureReason);
        return parent.WorkflowBlockReason is { } reason
            && reason == AutomationTaskStatuses.SafeSubmissionFailure(failed.LastAutomationStatus, failed.FailureReason)
            && !hasProviderReferences && (!rejection || failed.ProviderDiagnostic is not null);
    }

    private static FailureEvidence? ReadPreviousFailure(JsonArray history)
    {
        try
        {
            var failure = history.LastOrDefault()?.Deserialize<FailureEvidence>(JsonSerializerOptions.Web);
            return failure?.RetriedAtUtc is not null ? failure : null;
        }
        catch (JsonException)
        {
            throw new RequestProgressionHeldException(AmbiguousHold);
        }
    }

    internal static void ReleaseHold(Request parent, IReadOnlyList<RequestTask> tasks, DateTimeOffset now)
    {
        RequestHoldPolicy.ClearFailureHold(parent);
        var blocked = tasks.Where(task => task.Status is RequestTaskStatus.Failed or RequestTaskStatus.Cancelled)
            .OrderBy(task => task.Order).ThenBy(task => task.Id, StringComparer.Ordinal).FirstOrDefault();
        if (blocked is not null && !tasks.Any(task => AutomationTaskStatuses.RequiresManualReconciliation(task.LastAutomationStatus)))
        {
            var critical = blocked.Status == RequestTaskStatus.Failed
                && FailurePolicyEngine.ResolveFailurePolicy(blocked.FailurePolicy, blocked.IsCritical) == "FailRequest";
            var reason = AutomationTaskStatuses.SafeSubmissionFailure(blocked.LastAutomationStatus, blocked.FailureReason)
                ?? (blocked.Status == RequestTaskStatus.Cancelled ? $"Task '{blocked.Name}' was cancelled."
                    : critical ? $"Critical task '{blocked.Name}' failed." : $"Task '{blocked.Name}' failed.");
            RequestHoldPolicy.SetFailureHold(parent, blocked, critical ? "Failed" : "Blocked", reason, now);
            return;
        }

        RequestTaskStateService.ApplyTaskState(parent, tasks, now);
    }

    private sealed record FailureEvidence(string? AutomationAttemptCorrelationId, string? LastAutomationStatus, string? FailureReason,
        DateTimeOffset? StartedAt, DateTimeOffset? LastAutomationUpdatedAt, ProviderExecutionDiagnostic? ProviderDiagnostic,
        string? OrchestrationExternalRequestId, string? OrchestrationExternalRunId, string? OrchestratorExecutionId,
        DateTimeOffset? RetriedAtUtc = null, string? TaskId = null, string? RequestId = null, RequestTaskStatus? Status = null);
}
