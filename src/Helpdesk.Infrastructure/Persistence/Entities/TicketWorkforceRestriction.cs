namespace Helpdesk.Infrastructure.Persistence.Entities;

public sealed class TicketWorkforceRestriction
{
    public required string UserId { get; init; }
    public required string OrganizationId { get; init; }
    public required DateTime RestrictedUtc { get; init; }
}
