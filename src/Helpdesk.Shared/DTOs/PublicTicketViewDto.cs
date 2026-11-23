namespace Helpdesk.Shared.DTOs;

using Helpdesk.Shared.Models;

public class PublicTicketViewDto
{
    public string TrackingId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public TicketState State { get; set; } = TicketState.New;
    public DateTime? LastUpdatedAt { get; set; }
    public string? LastPublicNote { get; set; }
}
