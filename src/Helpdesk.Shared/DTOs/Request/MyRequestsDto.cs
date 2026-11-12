using Helpdesk.Shared.Models;

namespace Helpdesk.Shared.DTOs.Request;

public sealed class MyRequestListItemDto
{
    public string Id { get; set; } = string.Empty;
    public string TrackingId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public TicketState State { get; set; } = TicketState.New;
    public RequestFormReleaseStatus? ReleaseStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class MyRequestDetailDto
{
    public string Id { get; set; } = string.Empty;
    public string TrackingId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TicketState State { get; set; } = TicketState.New;
    public RequestFormReleaseStatus? ReleaseStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string? WorkflowStatus { get; set; }
    public string? WorkflowBlockReason { get; set; }
    public DateTimeOffset? WorkflowUpdatedAt { get; set; }
}

public sealed class MyRequestTaskDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public RequestTaskType Type { get; set; } = RequestTaskType.Manual;
    public RequestTaskStatus Status { get; set; } = RequestTaskStatus.Pending;
    public int Order { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public int RetryCount { get; set; }
    public string? FailureReason { get; set; }
    public string? LastAutomationStatus { get; set; }
    public List<MyRequestPendingApprovalDto> PendingApprovals { get; set; } = new();
}

public sealed class MyRequestPendingApprovalDto
{
    public string Id { get; set; } = string.Empty;
    public string RequestTaskId { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public string ApproverName { get; set; } = string.Empty;
    public string ApproverEmail { get; set; } = string.Empty;
    public string? OrganizationName { get; set; }
    public RequestTaskApprovalStatus Status { get; set; } = RequestTaskApprovalStatus.Pending;
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
}
