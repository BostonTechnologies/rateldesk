namespace Helpdesk.Shared.DTOs.Incident;

using Helpdesk.Shared.DTOs.Sla;
using Helpdesk.Shared.Models;

public class IncidentDto
{
    public string? OrganizationId { get; set; }

    // The Dodo-generated UUID, for internal use and navigation
    public string Id { get; set; } = string.Empty;

    // The new user-friendly tracking ID
    public string TrackingId { get; set; } = string.Empty;

    // Renamed from Title
    public string Subject { get; set; } = string.Empty;

    // Date of the last reply or status change
    public DateTime? UpdatedAt { get; set; }

    public DateTime? LastViewedByCustomerAt { get; set; }

    // The name of the customer's organization
    public string? CustomerOrgName { get; set; }

    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }

    // The name of the last person who replied
    public string? LastReplierName { get; set; }

    // The email address of the person who opened the ticket
    public string? RequesterEmail { get; set; }

    // Email addresses that should receive carbon-copy notifications
    public List<string> CcRecipients { get; set; } = new();

    public TicketState State { get; set; } = TicketState.New;
    public TicketPriority Priority { get; set; } = TicketPriority.Low;
    public List<Guid> CategoryIds { get; set; } = new();

    // Assigned technician/team member
    public string? AssignedToId { get; set; }

    public TicketSlaDto? Sla { get; set; }
}
