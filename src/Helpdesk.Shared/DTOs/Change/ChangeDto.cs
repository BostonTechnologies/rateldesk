namespace Helpdesk.Shared.DTOs.Change;

using Helpdesk.Shared.DTOs.Sla;
using Helpdesk.Shared.Models;

public class ChangeDto
{
    public string? OrganizationId { get; set; }
    public string? AssignedSupportGroupId { get; set; }
    public string? AssignedSupportGroupName { get; set; }
    public long QueueAssignmentVersion { get; set; }
    public string? OrganizationName { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TicketPriority Priority { get; set; } = TicketPriority.Low;
    public TicketState State { get; set; } = TicketState.New;
    public ChangeLifecycleState LifecycleState { get; set; } = ChangeLifecycleState.Draft;
    public bool? LifecycleChanged { get; set; }
    public string? LifecycleExplanation { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string TrackingId { get; set; } = string.Empty;
    public string? AssignedToId { get; set; }
    public string? LastReplierName { get; set; }
    public string? CustomerOrgName { get; set; }
    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? ChangeType { get; set; }
    public string? RequestedForUserId { get; set; }
    public string? RequestedForUserName { get; set; }
    public string? RequestedForUserEmail { get; set; }
    public string? ImplementorUserId { get; set; }
    public string? ImplementorUserName { get; set; }
    public string? ImplementorUserEmail { get; set; }
    public List<string> ApproverUserIds { get; set; } = new();
    public List<ChangeParticipantUserDto> Approvers { get; set; } = new();
    public List<ChangeApprovalDto> Approvals { get; set; } = new();
    public List<string> CcRecipients { get; set; } = new();
    public DateTime? ImplementationStartAt { get; set; }
    public DateTime? ImplementationEndAt { get; set; }
    public ChangeTemplateDto? ChangeTemplate { get; set; }
    public bool IsTemplateComplete { get; set; }
    public List<string> TemplateValidationErrors { get; set; } = new();
    public List<Guid> CategoryIds { get; set; } = new();
    public TicketSlaDto? Sla { get; set; }
}
