using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Infrastructure.Persistence.Entities;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;

namespace Helpdesk.Infrastructure.Ticketing;

public static class TicketQueueAssignment
{
    public static IQueryable<SupportGroup> Destinations(HelpdeskDbContext db, string organizationId) =>
        from queue in db.SupportGroups
        join owner in db.Organizations on queue.OwningOrganizationId equals owner.Id
        from customer in db.Organizations
        where queue.IsEnabled && owner.IsEnabled && customer.IsEnabled && customer.Id == organizationId &&
              (customer.Id == owner.Id || (customer.ItSupportOrganizationId == owner.Id &&
                  db.OrganizationSupportCoverages.Any(coverage => coverage.IsEnabled &&
                      coverage.CustomerOrganizationId == customer.Id && coverage.ProviderOrganizationId == owner.Id &&
                      coverage.SupportGroupId == queue.Id)))
        select queue;

    // Callers authorize the ticket, destination and assignee before staging.
    // The caller commits the ticket and transition in the same unit of work.
    public static void Stage(
        HelpdeskDbContext db, Ticket ticket, string? supportGroupId, string? assigneeId, string actorId, string? reason,
        DateTime occurredUtc)
    {
        if (ticket.OrganizationId is not { } organizationId)
            throw new InvalidOperationException("Queue assignment requires a ticket organization.");

        var previousGroupId = ticket.AssignedSupportGroupId;
        var previousAssigneeId = ticket.AssignedToId;
        if (previousGroupId != supportGroupId)
        {
            var version = checked(ticket.QueueAssignmentVersion + 1);
            db.TicketQueueTransitions.Add(new TicketQueueTransition
            {
                TicketId = ticket.Id,
                TicketType = ticket switch
                {
                    Incident => TicketType.Incident,
                    Request => TicketType.Request,
                    Change => TicketType.Change,
                    _ => throw new InvalidOperationException("Unsupported ticket module.")
                },
                OrganizationId = organizationId,
                AssignmentVersion = version,
                PreviousSupportGroupId = previousGroupId,
                AssignedSupportGroupId = supportGroupId,
                PreviousAssigneeId = previousAssigneeId,
                AssignedToId = assigneeId,
                ActorId = actorId,
                Reason = reason,
                OccurredUtc = occurredUtc
            });
            ticket.QueueAssignmentVersion = version;
        }
        ticket.AssignedSupportGroupId = supportGroupId;
        ticket.AssignedToId = assigneeId;
        if (ticket is Change change)
            change.ImplementorUserId = assigneeId;
        if (previousGroupId != supportGroupId || previousAssigneeId != assigneeId)
            ticket.UpdatedAt = occurredUtc;
    }
}
