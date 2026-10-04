using System.ComponentModel.DataAnnotations.Schema;

namespace Helpdesk.Shared.Models;

public class RequestTask : Ticket
{
    [NotMapped]
    public string Name
    {
        get => Title;
        set => Title = value;
    }

    public string RequestId { get; set; } = string.Empty;
    public Request? Request { get; set; }
    public string TemplateId { get; set; } = string.Empty;
    public RequestTaskType Type { get; set; } = RequestTaskType.Manual;
    public RequestTaskStatus Status { get; set; } = RequestTaskStatus.Pending;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? TaskSlaMinutes { get; set; }
    public int? EscalateAfterMinutes { get; set; }
    public string? EscalationUserId { get; set; }
    public string? EscalationRole { get; set; }
    public DateTimeOffset? SlaStartedAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public bool Escalated { get; set; }
    public DateTimeOffset? EscalatedAt { get; set; }
    public bool SlaBreached { get; set; }
    public bool IsBlocked { get; set; }
    public DateTimeOffset? UnblockedAt { get; set; }
    public string? ConditionExpression { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public string? FailureReason { get; set; }
    public bool IsCritical { get; set; }
    public string? FailurePolicy { get; set; }
    public int? MaxRetries { get; set; }
    public int? RetryDelayMinutes { get; set; }
    public string? AutomationBindingId { get; set; }
    public string? OrchestrationRequestDefinitionId { get; set; }
    public string? OrchestrationJobDefinitionId { get; set; }
    public string? OrchestrationExternalRequestId { get; set; }
    public string? OrchestrationExternalRunId { get; set; }
    public string? LastAutomationStatus { get; set; }
    public DateTimeOffset? LastAutomationUpdatedAt { get; set; }
    public int? ExpectedRuntimeSeconds { get; set; }
    public int? GraceSeconds { get; set; }
    public int? HardTimeoutSeconds { get; set; }
    public string? TimeoutIncidentId { get; set; }
    public string? OrchestratorExecutionId { get; set; }
    public string? OrchestrationLinkId { get; set; }
    public string? OrchestrationPeerInstanceId { get; set; }
    public long? OrchestrationLinkRevision { get; set; }
    public string? ResultJson { get; set; }
    public int Order { get; set; }
}
