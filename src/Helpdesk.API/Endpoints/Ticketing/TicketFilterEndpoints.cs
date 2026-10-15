using System.Security.Claims;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Ticketing;

internal static class TicketFilterEndpoints
{
    internal static void MapTicketFilterEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/filter-options", async (string module, string dimension, string? q, int? take,
            HttpRequest request, ClaimsPrincipal user, HelpdeskDbContext db, ICurrentUserAccessService accessService, CancellationToken token) =>
        {
            if (module is not ("incident" or "request" or "change") || dimension is not ("customer" or "for" or "assignee") || q?.Length > 200 || take is <= 0)
                return Results.BadRequest();
            var access = await accessService.ResolveAsync(user, token);
            IQueryable<Ticket> tickets = module switch {
                "incident" => TicketListQuery.Authorized(db.Incidents.AsNoTracking(), access, module),
                "request" => TicketListQuery.Authorized(db.Requests.AsNoTracking(), access, module),
                _ => TicketListQuery.Authorized(db.Changes.AsNoTracking(), access, module)
            };
            var selectedOrganizations = request.Query["customerOrgId"].ToArray();
            if (selectedOrganizations.Length > 20 || selectedOrganizations.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 200)) return Results.BadRequest();
            if (selectedOrganizations.Length > 0) tickets = tickets.Where(ticket => selectedOrganizations.Contains(ticket.OrganizationId));
            var organizationIds = tickets.Select(ticket => ticket.OrganizationId).Distinct();
            IQueryable<TicketFilterOptionDto> options;
            if (dimension == "customer")
                options = db.Organizations.AsNoTracking().Where(organization => organizationIds.Contains(organization.Id)).Select(organization => new TicketFilterOptionDto(organization.Id, organization.Name));
            else if (dimension == "for" && module != "change")
            {
                var ids = tickets.Select(ticket => ticket.CustomerId);
                options = db.Customers.AsNoTracking().Where(customer => ids.Contains(customer.Id)).Select(customer => new TicketFilterOptionDto(customer.Id, customer.Name + " (" + customer.Email + ")"));
            }
            else
            {
                IQueryable<User> users;
                if (dimension == "for")
                {
                    var ids = TicketListQuery.Authorized(db.Changes.AsNoTracking(), access, module)
                        .Where(change => selectedOrganizations.Length == 0 || selectedOrganizations.Contains(change.OrganizationId)).Select(change => change.RequestedForUserId);
                    users = db.Users.AsNoTracking().Where(candidate => ids.Contains(candidate.Id));
                }
                else users = AssigneeQueries.Eligible(db, module, db.Organizations.AsNoTracking().Where(organization => organizationIds.Contains(organization.Id)));
                users = AssigneeQueries.Search(db, users, q);
                options = users.Select(candidate => new TicketFilterOptionDto(candidate.Id, candidate.Name + " (" + candidate.Email + ")"));
            }
            if (dimension == "customer" || (dimension == "for" && module != "change"))
            {
                var pattern = "%" + (q ?? "").Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                options = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
                    ? options.Where(option => EF.Functions.ILike(option.Label, pattern, "\\"))
                    : options.Where(option => EF.Functions.Like(option.Label, pattern, "\\"));
            }
            var limit = Math.Clamp(take ?? 20, 1, 50);
            var items = await options.OrderBy(option => option.Label).ThenBy(option => option.Id).Take(limit + 1).ToListAsync(token);
            var hasMore = items.Count > limit;
            if (hasMore) items.RemoveAt(limit);
            return Results.Ok(new TicketFilterOptionsDto(items, hasMore));
        }).WithName("SearchTicketFilterOptions");
    }
}
