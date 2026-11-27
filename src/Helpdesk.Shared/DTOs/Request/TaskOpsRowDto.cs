using Helpdesk.Shared.Models;

namespace Helpdesk.Shared.DTOs.Request;

public sealed class TaskOpsRowDto
{
    public string TaskId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string? RequestTrackingId { get; set; }
    public string TaskName { get; set; } = string.Empty;
    public string? AssignedToId { get; set; }
    public string? AssignedToDisplayName { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public RequestTaskStatus Status { get; set; }
    public RequestTaskType Type { get; set; }
    public bool Escalated { get; set; }
    public bool IsCritical { get; set; }
    public string? FailurePolicy { get; set; }
    public string? FailureReason { get; set; }
    public Helpdesk.Shared.DTOs.Orchestration.ProviderExecutionDiagnostic? ProviderDiagnostic { get; set; }
    public int RetryCount { get; set; }
}
