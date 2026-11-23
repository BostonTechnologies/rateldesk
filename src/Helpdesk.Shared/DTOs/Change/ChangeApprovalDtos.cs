using Helpdesk.Shared.Models;

namespace Helpdesk.Shared.DTOs.Change;

public class ChangeApprovalDto
{
    public string Id { get; set; } = string.Empty;
    public string ChangeId { get; set; } = string.Empty;
    public string? ApproverId { get; set; }
    public string ApproverName { get; set; } = string.Empty;
    public string ApproverEmail { get; set; } = string.Empty;
    public ChangeApprovalStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? ViewedAtUtc { get; set; }
    public DateTimeOffset? TokenSentAtUtc { get; set; }
}

public class PublicChangeApprovalDto
{
    public string TrackingId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ChangeType { get; set; }
    public ChangeLifecycleState LifecycleState { get; set; }
    public string? OrganizationName { get; set; }
    public string? RequestedForName { get; set; }
    public DateTime? ImplementationStartAt { get; set; }
    public DateTime? ImplementationEndAt { get; set; }
    public string? ScopeOfChange { get; set; }
    public List<string> AffectedSystems { get; set; } = new();
    public List<string> ImplementationSteps { get; set; } = new();
    public List<string> ValidationSteps { get; set; } = new();
    public string? RollbackPlan { get; set; }
    public string? RollbackReference { get; set; }
    public bool CanReview { get; set; }
    public ChangeApprovalStatus ApprovalStatus { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public DateTimeOffset? ViewedAtUtc { get; set; }
}

public record ReviewChangeApprovalRequest(string TrackingId, string Email, string Token);
