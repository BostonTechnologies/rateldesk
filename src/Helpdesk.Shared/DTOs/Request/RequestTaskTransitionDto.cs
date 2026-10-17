using Helpdesk.Shared.Models;

namespace Helpdesk.Shared.DTOs.Request;

/// <summary>A committed task transition, without parent or navigation graphs.</summary>
public sealed class RequestTaskTransitionDto
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public TicketState State { get; init; } = TicketState.New;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; init; }
    public string? AssignedToId { get; init; }
    public string? CustomerId { get; init; }
    public string? OrganizationId { get; init; }
    public string? ServiceId { get; init; }
    public TicketPriority Priority { get; init; } = TicketPriority.Low;
    public List<string> LinkedAssetIds { get; init; } = new();
    public string? SlaPolicyId { get; init; }
    public DateTime? DueDate { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public TicketEmailExclusionReason EmailExclusionReason { get; init; } = TicketEmailExclusionReason.None;
    public List<string> Attachments { get; init; } = new();
    public string? RequesterEmail { get; init; }
    public List<string> CcRecipients { get; init; } = new();
    public DateTime? LastViewedByCustomerAt { get; init; }
    public string TrackingId { get; init; } = string.Empty;
    public int Replies { get; init; } = 0;
    public double TimeSpentHours { get; init; } = 0.0;
    public string? LastReplierName { get; init; }
    public string RequestId { get; init; } = string.Empty;
    public string TemplateId { get; init; } = string.Empty;
    public RequestTaskType Type { get; init; } = RequestTaskType.Manual;
    public RequestTaskStatus Status { get; init; } = RequestTaskStatus.Pending;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int? TaskSlaMinutes { get; init; }
    public int? EscalateAfterMinutes { get; init; }
    public string? EscalationUserId { get; init; }
    public string? EscalationRole { get; init; }
    public DateTimeOffset? SlaStartedAt { get; init; }
    public DateTimeOffset? DueAt { get; init; }
    public bool Escalated { get; init; }
    public DateTimeOffset? EscalatedAt { get; init; }
    public bool SlaBreached { get; init; }
    public bool IsBlocked { get; init; }
    public DateTimeOffset? UnblockedAt { get; init; }
    public string? ConditionExpression { get; init; }
    public int RetryCount { get; init; }
    public DateTimeOffset? NextRetryAt { get; init; }
    public string? FailureReason { get; init; }
    public bool IsCritical { get; init; }
    public string? FailurePolicy { get; init; }
    public int? MaxRetries { get; init; }
    public int? RetryDelayMinutes { get; init; }
    public string? AutomationBindingId { get; init; }
    public string? OrchestrationRequestDefinitionId { get; init; }
    public string? OrchestrationJobDefinitionId { get; init; }
    public string? OrchestrationExternalRequestId { get; init; }
    public string? OrchestrationExternalRunId { get; init; }
    public string? LastAutomationStatus { get; init; }
    public DateTimeOffset? LastAutomationUpdatedAt { get; init; }
    public int? ExpectedRuntimeSeconds { get; init; }
    public int? GraceSeconds { get; init; }
    public int? HardTimeoutSeconds { get; init; }
    public string? TimeoutIncidentId { get; init; }
    public string? OrchestratorExecutionId { get; init; }
    public string? OrchestrationLinkId { get; init; }
    public string? OrchestrationPeerInstanceId { get; init; }
    public long? OrchestrationLinkRevision { get; init; }
    public string? ResultJson { get; init; }
    public int Order { get; init; }
    public string Name { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
}
