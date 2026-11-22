using Dodo.Primitives;

namespace Helpdesk.Shared.Models;

public abstract class Ticket
{
    public string Id { get; set; } = Uuid.CreateVersion7().ToString();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TicketState State { get; set; } = TicketState.New;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public string? AssignedToId { get; set; }
    public string? CustomerId { get; set; }
    public string? OrganizationId { get; set; }
    public string? ServiceId { get; set; }
    public TicketPriority Priority { get; set; } = TicketPriority.Low;
    public List<string> LinkedAssetIds { get; set; } = new();
    public string? SlaPolicyId { get; set; }
    public DateTime? DueDate { get; set; }
    public string? AdministrativeClosureReason { get; set; }
    public string? AdministrativelyClosedBy { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public TicketEmailExclusionReason EmailExclusionReason { get; set; } = TicketEmailExclusionReason.None;
    public List<string> Attachments { get; set; } = new();
    /// <summary>
    /// Plain email address of the requester who opened the ticket.
    /// </summary>
    public string? RequesterEmail { get; set; }
    /// <summary>
    /// Email addresses that should receive carbon-copy notifications for this ticket.
    /// </summary>
    public List<string> CcRecipients { get; set; } = new();
    public DateTime? LastViewedByCustomerAt { get; set; }

    // This is the new user-facing ID like 'INC-ABC-123'
    public string TrackingId { get; set; } = string.Empty;


    // Counter for user/technician replies
    public int Replies { get; set; } = 0;

    // Cumulative time spent on the ticket, in hours
    public double TimeSpentHours { get; set; } = 0.0;

    // Name of the last person to add a worklog/reply
    public string? LastReplierName { get; set; }
}
