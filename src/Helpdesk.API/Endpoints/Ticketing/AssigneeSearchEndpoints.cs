using System.Security.Claims;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.User;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Ticketing;

internal static class AssigneeSearchEndpoints
{
    internal static void MapAssigneeSearchEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/assignees/search", async (ClaimsPrincipal user,
            [FromQuery] string module, [FromQuery] string organizationId,
            [FromQuery] string? q, [FromQuery] int? limit,
            HelpdeskDbContext db, ICurrentUserAccessService accessService, CancellationToken token) =>
        {
            if (!ValidModule(module) || string.IsNullOrWhiteSpace(organizationId) || q?.Length > 200 || limit < 1)
                return Results.BadRequest("Use a ticket module, organization, query up to 200 characters and a positive limit.");
            var access = await accessService.ResolveAsync(user, token);
            if (!CanManage(access, module, organizationId)) return Results.Forbid();
            var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == organizationId, token);
            if (organization is null) return Results.NotFound();

            var take = Math.Min(limit ?? 20, 50);
            var items = await AssigneeQueries.Search(db, AssigneeQueries.Eligible(db, module, organization), q)
                .OrderBy(x => x.Name.ToLower()).ThenBy(x => x.Email.ToLower()).ThenBy(x => x.Id)
                .Select(x => new UserDto(x.Id, x.Name, x.Email, x.Role, x.IsTestUser, x.OrganizationId))
                .Take(take + 1).ToListAsync(token);
            var hasMore = items.Count > take;
            if (hasMore) items.RemoveAt(take);
            return Results.Ok(new AssigneeSearchResult(items, hasMore));
        }).WithName("SearchTicketAssignees");

        // Resolve only an existing ticket value; this cannot expose arbitrary directory IDs
        // or turn a no-longer-eligible assignment into a new candidate.
        group.MapGet("/assignees/current", async (ClaimsPrincipal user,
            [FromQuery] string module, [FromQuery] string organizationId, [FromQuery] string ticketId,
            HelpdeskDbContext db, ICurrentUserAccessService accessService, CancellationToken token) =>
        {
            if (!ValidModule(module) || string.IsNullOrWhiteSpace(organizationId) || string.IsNullOrWhiteSpace(ticketId))
                return Results.BadRequest();
            var access = await accessService.ResolveAsync(user, token);
            Ticket? ticket = module switch
            {
                "incident" => await db.Incidents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == ticketId, token),
                "request" => await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == ticketId, token),
                "change" => await db.Changes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == ticketId, token),
                _ => null
            };
            if (ticket is null) return Results.NotFound();
            var canView = module switch
            {
                "incident" => access.CanViewIncident(ticket.OrganizationId, ticket.CustomerId, ticket.RequesterEmail),
                "request" => access.CanViewRequest(ticket.OrganizationId, ticket.CustomerId, ticket.RequesterEmail),
                _ => access.CanViewChange(ticket.OrganizationId, ticket.CustomerId, ticket.RequesterEmail)
            };
            if (ticket.OrganizationId != organizationId || !canView) return Results.Forbid();
            var id = ticket is Change change ? change.ImplementorUserId : ticket.AssignedToId;
            var current = await db.Users.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new UserDto(x.Id, x.Name, x.Email, x.Role, x.IsTestUser, x.OrganizationId))
                .SingleOrDefaultAsync(token);
            return Results.Ok(current);
        }).WithName("GetCurrentTicketAssignee");
    }

    private static bool ValidModule(string module) => module is "incident" or "request" or "change";
    private static bool CanManage(CurrentUserAccessProfile access, string module, string organizationId) => module switch
    {
        "incident" => access.CanManageIncident(organizationId),
        "request" => access.CanManageRequest(organizationId),
        "change" => access.CanManageChange(organizationId),
        _ => false
    };
}
