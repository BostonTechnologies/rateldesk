namespace Helpdesk.Shared.DTOs.Request;

using Helpdesk.Shared.DTOs.Sla;
using Helpdesk.Shared.Models;

public class RequestDto
{
    public string? OrganizationId { get; set; }
    public string? AdministrativeClosureReason { get; set; }
    public long StateVersion { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TicketPriority Priority { get; set; } = TicketPriority.Low;
    public TicketState State { get; set; } = TicketState.New;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string TrackingId { get; set; } = string.Empty;
    public string? AssignedToId { get; set; }
    public string? LastReplierName { get; set; }
    public string? CustomerOrgName { get; set; }
    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public List<Guid> CategoryIds { get; set; } = new();
    public string? ServiceId { get; set; }
    public string? RequestFormId { get; set; }
    public string? PayloadJson { get; set; }
    public string? SourceTicketId { get; set; }
    public string? SourceTicketTrackingId { get; set; }
    public string? SourceAutomationBindingId { get; set; }
    public string? WorkflowStatus { get; set; }
    public string? WorkflowBlockReason { get; set; }
    public DateTimeOffset? WorkflowUpdatedAt { get; set; }
    public TicketSlaDto? Sla { get; set; }
}
