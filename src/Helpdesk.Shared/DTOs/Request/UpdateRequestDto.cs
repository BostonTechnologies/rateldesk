namespace Helpdesk.Shared.DTOs.Request;

using Helpdesk.Shared.Models;

public class UpdateRequestDto
{
    public TicketState? State { get; set; }
    public TicketPriority? Priority { get; set; }
    public List<Guid>? CategoryIds { get; set; }
}
