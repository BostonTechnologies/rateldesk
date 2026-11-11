using Helpdesk.Shared.Models;

namespace Helpdesk.Shared.DTOs.Request;

public sealed class RequestTaskListItemDto
{
    public string Id { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string RequestTrackingId { get; set; } = string.Empty;
    public string RequestTitle { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public RequestTaskType Type { get; set; } = RequestTaskType.Manual;
    public RequestTaskStatus Status { get; set; } = RequestTaskStatus.Pending;
    public string? AssignedToId { get; set; }
    public string? AssignedToDisplayName { get; set; }
    public int Order { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public int RetryCount { get; set; }
    public string? FailureReason { get; set; }
    public string? OrchestrationExternalRequestId { get; set; }
    public string? OrchestrationExternalRunId { get; set; }
    public DateTimeOffset? LastAutomationUpdatedAt { get; set; }
    public string? LastAutomationStatus { get; set; }
    public int? ExpectedRuntimeSeconds { get; set; }
    public int? GraceSeconds { get; set; }
    public int? HardTimeoutSeconds { get; set; }
    public long? AutomationDeadlineUtcTicks { get; set; }
    public DateTimeOffset? AutomationTimedOutAt { get; set; }
    public string? AutomationTimedOutAttemptId { get; set; }
    public string? AutomationAttemptCorrelationId { get; set; }
    public string? OrchestrationLinkId { get; set; }
    public long? OrchestrationLinkRevision { get; set; }
    public string? TimeoutIncidentId { get; set; }
    public bool? AutomationBindingEnabled { get; set; }
    public AutomationBindingSyncState? AutomationBindingSyncState { get; set; }
    public bool AutomationReadyToStart { get; set; } = true;
    public string? AutomationBlockReason { get; set; }
}

public sealed class BulkRequestTaskActionDto
{
    public List<string> Ids { get; set; } = new();
}
