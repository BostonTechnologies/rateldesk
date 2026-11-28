using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;

namespace Helpdesk.API.Endpoints.Ticketing;

internal static class TicketAssigneeValidation
{
    internal static async Task<IResult?> GroupAsync(HelpdeskDbContext db, ICurrentUserAccessService accessService,
        TicketType module, Ticket ticket, string? userId, CancellationToken token)
    {
        if (ticket.AssignedSupportGroupId is not { } groupId || string.IsNullOrWhiteSpace(userId)) return null;
        return ticket.OrganizationId is { } organizationId &&
            await AssigneeQueries.CanAssignToQueueAsync(db, accessService, module, organizationId, groupId, userId.Trim(), token)
            ? null : Results.BadRequest("The selected assignee is unavailable for this queue.");
    }

    internal static async Task<IResult?> RequestAsync(HelpdeskDbContext db, string? organizationId, string? userId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        if (string.IsNullOrWhiteSpace(organizationId)) return Results.BadRequest("Organization is required when assigning a ticket.");
        return await AssigneeQueries.CanAssignAsync(db, "request", organizationId, userId.Trim(), token)
            ? null : Results.BadRequest("The selected user is no longer eligible for this organization. Choose another assignee.");
    }
}
