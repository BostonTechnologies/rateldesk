using System.ComponentModel.DataAnnotations.Schema;

namespace Helpdesk.Shared.Models;

public class Request : Ticket
{
    public ICollection<RequestCategoryLink> CategoryLinks { get; set; } = new List<RequestCategoryLink>();
    public ICollection<RequestTask> Tasks { get; set; } = new List<RequestTask>();

    [Column("Category")]
    public string? LegacyCategory { get; set; }
    public string? SourceTicketId { get; set; }
    public string? SourceAutomationBindingId { get; set; }
    public string? RequestFormId { get; set; }
    public string? PayloadJson { get; set; }
    public string? SelfServiceSubmissionId { get; set; }
    public string? SelfServiceSubmitterId { get; set; }
    public string? SelfServiceSubmissionHash { get; set; }
    public string? WorkflowStatus { get; set; }
    public string? WorkflowBlockReason { get; set; }
    public DateTimeOffset? WorkflowUpdatedAt { get; set; }
}
