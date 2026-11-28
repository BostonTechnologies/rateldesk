using Dodo.Primitives;
using Helpdesk.Shared.Enums;

namespace Helpdesk.Infrastructure.Persistence.Entities;

public sealed class TicketQueueTransition
{
    public string Id { get; init; } = Uuid.CreateVersion7().ToString();
    public required string TicketId { get; init; }
    public required TicketType TicketType { get; init; }
    public required string OrganizationId { get; init; }
    public required long AssignmentVersion { get; init; }
    public string? PreviousSupportGroupId { get; init; }
    public string? AssignedSupportGroupId { get; init; }
    public string? PreviousAssigneeId { get; init; }
    public string? AssignedToId { get; init; }
    public required string ActorId { get; init; }
    public string? Reason { get; init; }
    public required DateTime OccurredUtc { get; init; }
}
