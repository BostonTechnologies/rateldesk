using Helpdesk.Infrastructure.Persistence;

namespace Helpdesk.API.Endpoints.Ticketing;

internal static class TicketAssigneeValidation
{
    internal static async Task<IResult?> RequestAsync(HelpdeskDbContext db, string? organizationId, string? userId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        if (string.IsNullOrWhiteSpace(organizationId)) return Results.BadRequest("Organization is required when assigning a ticket.");
        return await AssigneeQueries.CanAssignAsync(db, "request", organizationId, userId.Trim(), token)
            ? null : Results.BadRequest("The selected user is no longer eligible for this organization. Choose another assignee.");
    }
}
