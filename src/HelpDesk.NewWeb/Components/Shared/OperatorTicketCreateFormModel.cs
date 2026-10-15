namespace HelpDesk.NewWeb.Components.Shared;

using Helpdesk.Shared.Models;

public sealed class OperatorTicketCreateFormModel
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TicketPriority Priority { get; set; } = TicketPriority.Low;
    public string? OrganizationId { get; set; }
    public string? CustomerId { get; set; }
    public HashSet<Guid> CategoryIds { get; set; } = new();
    public string? AssignedToId { get; set; }
}
