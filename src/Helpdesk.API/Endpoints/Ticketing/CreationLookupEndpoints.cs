using System.Security.Claims;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Change;
using Helpdesk.Shared.DTOs.Customer;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EntityState = Helpdesk.Shared.Models.EntityState;

namespace Helpdesk.API.Endpoints.Ticketing;

internal static class CreationLookupEndpoints
{
    internal static void MapCreationLookupEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/organizations/search", async (ClaimsPrincipal user,
            [FromQuery] string module, [FromQuery] string? q, [FromQuery] int? limit, [FromQuery] string? id,
            HelpdeskDbContext db, ICurrentUserAccessService accessService, CancellationToken token) =>
        {
            if (!Valid(module, q, limit)) return Results.BadRequest("Use a supported scope, a query up to 200 characters and a positive limit.");
            var access = await accessService.ResolveAsync(user, token);
            var allowed = access.AllowedOrganizationIds.Where(candidate => CanUse(access, candidate, module)).ToArray();
            var eligible = db.Organizations.AsNoTracking().Where(x => x.State == EntityState.Enabled);
            if (!access.IsHelpdeskAdmin) eligible = eligible.Where(x => allowed.Contains(x.Id));
            var total = await eligible.CountAsync(token);
            if (!string.IsNullOrWhiteSpace(id)) eligible = eligible.Where(x => x.Id == id);
            else if (!string.IsNullOrWhiteSpace(q))
            {
                var pattern = Pattern(db, q);
                eligible = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
                    ? eligible.Where(x => EF.Functions.ILike(x.Name, pattern, "\\"))
                    : eligible.Where(x => EF.Functions.Like(SqliteUnicodeSearch.Fold(x.Name)!, pattern, "\\"));
            }
            var take = Math.Min(limit ?? 20, 50);
            var items = await eligible.OrderBy(x => x.Name.ToLower()).ThenBy(x => x.Id)
                .Select(x => new OrganizationDto { Id = x.Id, Name = x.Name, DnsName = x.DnsName, State = x.State })
                .Take(take + 1).ToListAsync(token);
            return Results.Ok(Page(items, total, take));
        }).WithName("SearchCreationOrganizations");

        group.MapGet("/customers/search", async (ClaimsPrincipal user,
            [FromQuery] string module, [FromQuery] string organizationId,
            [FromQuery] string? q, [FromQuery] int? limit, [FromQuery] string? id,
            HelpdeskDbContext db, ICurrentUserAccessService accessService, CancellationToken token) =>
        {
            if (!Valid(module, q, limit) || module == "data-management" || string.IsNullOrWhiteSpace(organizationId)) return Results.BadRequest();
            var access = await accessService.ResolveAsync(user, token);
            if (!CanUse(access, organizationId, module)) return Results.Forbid();
            var eligible = from customer in db.Customers.AsNoTracking()
                           join organization in db.Organizations.AsNoTracking() on customer.OrganizationId equals organization.Id
                           where customer.State == EntityState.Enabled && organization.State == EntityState.Enabled && customer.OrganizationId == organizationId
                           select new CustomerDto { Id = customer.Id, Name = customer.Name, Email = customer.Email,
                               OrganizationId = customer.OrganizationId, OrganizationName = organization.Name, State = customer.State };
            if (!CanManage(access, organizationId, module)) eligible = eligible.Where(x => x.Id == access.CustomerId);
            var total = await eligible.CountAsync(token);
            if (!string.IsNullOrWhiteSpace(id)) eligible = eligible.Where(x => x.Id == id);
            else if (!string.IsNullOrWhiteSpace(q))
            {
                var pattern = Pattern(db, q);
                eligible = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
                    ? eligible.Where(x => EF.Functions.ILike(x.Name, pattern, "\\") || EF.Functions.ILike(x.Email, pattern, "\\"))
                    : eligible.Where(x => EF.Functions.Like(SqliteUnicodeSearch.Fold(x.Name)!, pattern, "\\") || EF.Functions.Like(SqliteUnicodeSearch.Fold(x.Email)!, pattern, "\\"));
            }
            var take = Math.Min(limit ?? 20, 50);
            var items = await eligible.OrderBy(x => x.Name.ToLower()).ThenBy(x => x.Email.ToLower()).ThenBy(x => x.Id).Take(take + 1).ToListAsync(token);
            return Results.Ok(Page(items, total, take));
        }).WithName("SearchCreationCustomers");

        // Creation deliberately has its own scoped lookup. The organization administration
        // endpoint remains HelpdeskAdmin-only, and implementors retain their separate lookup.
        group.MapGet("/change-participants/search", async (ClaimsPrincipal user,
            [FromQuery] string organizationId, [FromQuery] string? q, [FromQuery] int? limit, [FromQuery] string? id,
            HelpdeskDbContext db, ICurrentUserAccessService accessService, CancellationToken token) =>
        {
            if (!Valid("change", q, limit) || string.IsNullOrWhiteSpace(organizationId)) return Results.BadRequest();
            var access = await accessService.ResolveAsync(user, token);
            if (!access.CanCreateChange(organizationId, null, null)) return Results.Forbid();
            if (!await db.Organizations.AsNoTracking().AnyAsync(x => x.Id == organizationId && x.State == EntityState.Enabled, token)) return Results.NotFound();
            var users = db.Users.AsNoTracking().Where(x => !x.IsDeleted && x.OrganizationId == organizationId)
                .Select(x => new { x.Id, x.Name, x.Email, x.Role, x.OrganizationId });
            var contacts = db.Customers.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.State == EntityState.Enabled && !db.Users.Any(u => u.Id == x.Id && u.OrganizationId == organizationId))
                .Select(x => new { x.Id, x.Name, x.Email, Role = "Customer", OrganizationId = (string?)x.OrganizationId });
            var eligible = users.Concat(contacts);
            var total = await eligible.CountAsync(token);
            if (!string.IsNullOrWhiteSpace(id)) eligible = eligible.Where(x => x.Id == id);
            else if (!string.IsNullOrWhiteSpace(q))
            {
                var pattern = Pattern(db, q);
                eligible = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
                    ? eligible.Where(x => EF.Functions.ILike(x.Name, pattern, "\\") || EF.Functions.ILike(x.Email, pattern, "\\"))
                    : eligible.Where(x => EF.Functions.Like(SqliteUnicodeSearch.Fold(x.Name)!, pattern, "\\") || EF.Functions.Like(SqliteUnicodeSearch.Fold(x.Email)!, pattern, "\\"));
            }
            var take = Math.Min(limit ?? 20, 50);
            var items = await eligible.OrderBy(x => x.Name.ToLower()).ThenBy(x => x.Email.ToLower()).ThenBy(x => x.Id)
                .Take(take + 1).Select(x => new ChangeParticipantUserDto(x.Id, x.Name, x.Email, x.Role, x.OrganizationId)).ToListAsync(token);
            return Results.Ok(Page(items, total, take));
        }).WithName("SearchCreationChangeParticipants");
    }

    private static CreationLookupResult<T> Page<T>(List<T> items, int total, int take)
    {
        var more = items.Count > take;
        if (more) items.RemoveAt(take);
        return new(items, total, more);
    }

    private static bool Valid(string module, string? q, int? limit) =>
        module is "incident" or "request" or "change" or "data-management" && q?.Length is not > 200 && limit is not < 1;
    private static string Pattern(HelpdeskDbContext db, string term)
    {
        if (db.Database.IsSqlite())
        {
            SqliteUnicodeSearch.RegisterConnection(db);
            term = SqliteUnicodeSearch.Fold(term)!;
        }
        return "%" + term.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
    }
    private static bool CanUse(CurrentUserAccessProfile access, string id, string module) =>
        CanManage(access, id, module) || module switch
        {
            "incident" => access.HasPermission(HelpdeskPermissions.IncidentUser, id),
            "request" => access.HasPermission(HelpdeskPermissions.RequestUser, id),
            "change" => access.CanCreateChange(id, null, null),
            _ => false
        };
    private static bool CanManage(CurrentUserAccessProfile access, string id, string module) => module switch
    {
        "incident" => access.CanManageIncident(id),
        "request" => access.CanManageRequest(id),
        "change" => access.CanManageChange(id),
        "data-management" => access.HasPermission(HelpdeskPermissions.DataManagementAdmin, id),
        _ => false
    };
}
