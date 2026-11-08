namespace Helpdesk.Shared.DTOs.Change;

using Helpdesk.Shared.Models;
using System.ComponentModel.DataAnnotations;

public class CreateChangeDto
{
    public bool SaveAsDraft { get; set; }
    [Required]
    public string Title { get; set; } = string.Empty;
    [Required]
    public string Description { get; set; } = string.Empty;
    public TicketPriority Priority { get; set; } = TicketPriority.Low;
    public string? OrganizationId { get; set; }
    public string? RequestedForUserId { get; set; }
    public string? ImplementorUserId { get; set; }
    public List<string>? ApproverUserIds { get; set; }
    public List<string>? LinkedAssetIds { get; set; }
    public List<string>? Attachments { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? ImplementationStartAt { get; set; }
    public DateTime? ImplementationEndAt { get; set; }
    [Required]
    public string ChangeType { get; set; } = string.Empty;
    [Required]
    public ChangeTemplateDto ChangeTemplate { get; set; } = new();
    public List<Guid>? CategoryIds { get; set; }
}
