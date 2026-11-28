namespace Helpdesk.Shared.DTOs.Incident;

using Helpdesk.Shared.Models;

public class CreateIncidentDto
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TicketPriority Priority { get; set; } = TicketPriority.Low;
    public string? CustomerId { get; set; }
    public string? OrganizationId { get; set; }
    public string? AssignedToId { get; set; }
    public List<string>? LinkedAssetIds { get; set; }
    public List<string>? Attachments { get; set; }
    public DateTime? DueDate { get; set; }
    public string? Impact { get; set; }

    /// <summary>
    /// Optional CC recipients to be added as incident listeners.
    /// </summary>
    public List<string>? CcRecipients { get; set; }

    /// <summary>Email of the requester parsed from inbound email "From".</summary>
    public string? RequesterEmail { get; set; }

    public List<Guid>? CategoryIds { get; set; }
}

public sealed class CreateQueuedIncidentDto : CreateIncidentDto
{
    public string? AssignedSupportGroupId { get; set; }
}
