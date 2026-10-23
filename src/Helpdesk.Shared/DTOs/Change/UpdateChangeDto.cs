namespace Helpdesk.Shared.DTOs.Change;

using Helpdesk.Shared.Models;

public class UpdateChangeDto
{
    public TicketState? State { get; set; }
    public ChangeLifecycleState? LifecycleState { get; set; }
    public TicketPriority? Priority { get; set; }
    public string? OrganizationId { get; set; }
    public string? RequestedForUserId { get; set; }
    public string? ImplementorUserId { get; set; }
    public List<string>? ApproverUserIds { get; set; }
    public DateTime? ImplementationStartAt { get; set; }
    public DateTime? ImplementationEndAt { get; set; }
    public List<Guid>? CategoryIds { get; set; }
    public string? ChangeType { get; set; }
    public ChangeTemplateDto? ChangeTemplate { get; set; }
}
