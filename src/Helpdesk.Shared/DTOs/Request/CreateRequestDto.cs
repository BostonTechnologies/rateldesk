namespace Helpdesk.Shared.DTOs.Request;

using Helpdesk.Shared.Models;
using System.ComponentModel.DataAnnotations;

public class CreateRequestDto
{
    public string? AssignedSupportGroupId { get; set; }
    public string? Title { get; set; }
    [Required]
    public string Description { get; set; } = string.Empty;
    public TicketPriority Priority { get; set; } = TicketPriority.Low;
    public string? CustomerId { get; set; }
    public string? OrganizationId { get; set; }
    public string? AssignedToId { get; set; }
    public List<string>? LinkedAssetIds { get; set; }
    public List<string>? Attachments { get; set; }
    public DateTime? DueDate { get; set; }
    public string? Category { get; set; }
    public List<Guid>? CategoryIds { get; set; }
    public string? ServiceId { get; set; }
    public string? RequestFormId { get; set; }
    public string? PayloadJson { get; set; }
    public string? RequesterEmail { get; set; }
    public List<string>? CcRecipients { get; set; }
}
