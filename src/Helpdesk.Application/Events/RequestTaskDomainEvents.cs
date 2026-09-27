using Helpdesk.Shared.Models;

namespace Helpdesk.Application.Events;

public static class RequestTaskDomainEventTypes
{
    public const string Generated = "DomainEvent.RequestTask.Generated";
    public const string Started = "DomainEvent.RequestTask.Started";
    public const string Completed = "DomainEvent.RequestTask.Completed";
    public const string Failed = "DomainEvent.RequestTask.Failed";
    public const string RetryRequested = "DomainEvent.RequestTask.RetryRequested";
    public const string RetryScheduled = "DomainEvent.RequestTask.RetryScheduled";
    public const string RetryTriggered = "DomainEvent.RequestTask.RetryTriggered";
    public const string AutomationSubmitted = "DomainEvent.RequestTask.AutomationSubmitted";
    public const string AutomationRunning = "DomainEvent.RequestTask.AutomationRunning";
    public const string AutomationCompleted = "DomainEvent.RequestTask.AutomationCompleted";
    public const string AutomationFailed = "DomainEvent.RequestTask.AutomationFailed";
    public const string AutomationSubmitFailed = "DomainEvent.RequestTask.AutomationSubmitFailed";
    public const string AutomationSubmitRejected = "DomainEvent.RequestTask.AutomationSubmitRejected";
    public const string AutomationSubmitUncertain = "DomainEvent.RequestTask.AutomationSubmitUncertain";
    public const string Blocked = "DomainEvent.RequestTask.Blocked";
    public const string Unblocked = "DomainEvent.RequestTask.Unblocked";
    public const string Skipped = "DomainEvent.RequestTask.Skipped";
    public const string SlaStarted = "DomainEvent.RequestTask.SlaStarted";
    public const string Escalated = "DomainEvent.RequestTask.Escalated";
    public const string SlaBreached = "DomainEvent.RequestTask.SlaBreached";
    public const string RequestStateChanged = "DomainEvent.RequestTask.RequestStateChanged";
}

public sealed record RequestTasksGeneratedEvent(
    string RequestId,
    int TaskCount,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.Generated,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: RequestId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskStartedEvent(
    string TaskId,
    string RequestId,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.Started,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: RequestId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskCompletedEvent(
    string TaskId,
    string RequestId,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.Completed,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: RequestId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskFailedEvent(
    string TaskId,
    string RequestId,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.Failed,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: RequestId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskRetryRequestedEvent(
    string TaskId,
    string RequestId,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.RetryRequested,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: RequestId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskRetryScheduledEvent(
    string TaskId,
    string RequestId,
    string TaskName,
    bool IsCritical,
    string FailurePolicy,
    int RetryCount,
    DateTimeOffset? NextRetryAt,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.RetryScheduled,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: TaskId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskRetryTriggeredEvent(
    string TaskId,
    string RequestId,
    string TaskName,
    bool IsCritical,
    string FailurePolicy,
    int RetryCount,
    DateTimeOffset? NextRetryAt,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.RetryTriggered,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: TaskId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskBlockedEvent(
    string TaskId,
    string RequestId,
    IReadOnlyCollection<string> DependsOnIds,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.Blocked,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: TaskId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskUnblockedEvent(
    string TaskId,
    string RequestId,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.Unblocked,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: TaskId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskSkippedEvent(
    string TaskId,
    string RequestId,
    string ConditionExpression,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.Skipped,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: TaskId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskSlaStartedEvent(
    string TaskId,
    string RequestId,
    DateTimeOffset DueAt,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.SlaStarted,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: TaskId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskEscalatedEvent(
    string TaskId,
    string RequestId,
    DateTimeOffset? DueAt,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.Escalated,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: TaskId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskSlaBreachedEvent(
    string TaskId,
    string RequestId,
    DateTimeOffset? DueAt,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.SlaBreached,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: TaskId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskAutomationSubmittedEvent(
    string TaskId,
    string RequestId,
    string ExecutionId,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.AutomationSubmitted,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: ExecutionId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskAutomationRunningEvent(
    string TaskId,
    string RequestId,
    string? ExecutionId,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId,
    string? CallerClientId = null)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.AutomationRunning,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: string.IsNullOrWhiteSpace(ExecutionId) ? TaskId : ExecutionId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskAutomationCompletedEvent(
    string TaskId,
    string RequestId,
    string? ExecutionId,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId,
    string? CallerClientId = null)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.AutomationCompleted,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: string.IsNullOrWhiteSpace(ExecutionId) ? TaskId : ExecutionId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskAutomationFailedEvent(
    string TaskId,
    string RequestId,
    string? ExecutionId,
    string Reason,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId,
    string? CallerClientId = null)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.AutomationFailed,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: string.IsNullOrWhiteSpace(ExecutionId) ? TaskId : ExecutionId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskAutomationSubmitFailedEvent(
    string TaskId,
    string RequestId,
    string Reason,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.AutomationSubmitFailed,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: TaskId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskAutomationSubmitUncertainEvent(
    string TaskId,
    string RequestId,
    string Reason,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.AutomationSubmitUncertain,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: TaskId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestTaskAutomationSubmitRejectedEvent(
    string TaskId,
    string RequestId,
    string Reason,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.AutomationSubmitRejected,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: TaskId,
        OccurredUtc: Timestamp.UtcDateTime);

public sealed record RequestStateChangedEvent(
    string RequestId,
    TicketState PreviousState,
    TicketState NewState,
    string? TenantId,
    DateTimeOffset Timestamp,
    string CorrelationId)
    : DomainEvent(
        EventType: RequestTaskDomainEventTypes.RequestStateChanged,
        Source: "RequestTask",
        TenantId: TenantId,
        EntityId: RequestId,
        CorrelationId: CorrelationId,
        Reference: RequestId,
        OccurredUtc: Timestamp.UtcDateTime);
