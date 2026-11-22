using Helpdesk.Shared.DTOs;
using Helpdesk.API.Endpoints.Organizations;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Organization;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Organization;

public static class AdminTenantEndpoints
{
    public static void MapAdminTenantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapAdminDirectoryEndpoints();
        var group = app.MapGroup("/api/v1/admin")
            .WithTags("Admin")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/tenants", async (HelpdeskDbContext db) =>
        {
            var tenants = await db.Organizations
                .Where(x => x.State == Helpdesk.Shared.Models.EntityState.Enabled)
                .Select(x => new TenantLookupDto
                {
                    Id = x.Id,
                    Name = x.Name
                })
                .OrderBy(x => x.Name)
                .ToListAsync();

            return Results.Ok(tenants);
        });

        group.MapGet("/tenants/{organizationId}/report-recipients/search", async (string organizationId, string? q,
            int? limit, HelpdeskDbContext db, CancellationToken ct) =>
        {
            if (q?.Length > 200 || limit is < 1) return Results.BadRequest();
            var users = db.Users.AsNoTracking()
                .Where(row => row.OrganizationId == organizationId && row.Email.Trim() != "")
                .Select(row => new { Email = row.Email.Trim(), row.Name });
            var contacts = db.Customers.AsNoTracking()
                .Where(row => row.OrganizationId == organizationId && row.State == Helpdesk.Shared.Models.EntityState.Enabled && row.Email.Trim() != "")
                .Select(row => new { Email = row.Email.Trim(), row.Name });
            var directory = users.Concat(contacts);
            var total = await directory.Select(row => row.Email.ToUpper()).Distinct().CountAsync(ct);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToUpperInvariant();
                if (db.Database.IsSqlite())
                {
                    SqliteUnicodeSearch.RegisterConnection(db);
                    directory = directory.Where(row => SqliteUnicodeSearch.Fold(row.Name)!.Contains(term) || SqliteUnicodeSearch.Fold(row.Email)!.Contains(term));
                }
                else directory = directory.Where(row => row.Name.ToUpper().Contains(term) || row.Email.ToUpper().Contains(term));
            }
            var take = Math.Min(limit ?? 20, 50);
            var rows = await directory.GroupBy(row => row.Email.ToUpper())
                .Select(group => new { Email = group.Min(row => row.Email)!, Name = group.Min(row => row.Name)! })
                .OrderBy(row => row.Name).ThenBy(row => row.Email).Take(take + 1).ToListAsync(ct);
            var items = rows.Take(take).Select(row => new TicketFilterOptionDto(row.Email,
                string.IsNullOrWhiteSpace(row.Name) ? row.Email : $"{row.Name} ({row.Email})")).ToList();
            return Results.Ok(new CreationLookupResult<TicketFilterOptionDto>(items, total, rows.Count > take));
        });

        group.MapGet("/tenants/search", async (HelpdeskDbContext db, string? q, string? id, string? exclude, int? limit, CancellationToken ct) =>
        {
            var eligible = db.Organizations.AsNoTracking().Where(x => x.State == Helpdesk.Shared.Models.EntityState.Enabled);
            if (!string.IsNullOrWhiteSpace(exclude)) eligible = eligible.Where(x => x.Id != exclude);
            return await OrganizationLookup.SearchAsync(db, eligible, q, id, limit, ct);
        });

        group.MapGet("/tenants/lookup", async (HelpdeskDbContext db) =>
        {
            var tenants = await db.Organizations
                .Where(x => x.State == Helpdesk.Shared.Models.EntityState.Enabled)
                .Select(x => new TenantLookupDto
                {
                    Id = x.Id,
                    Name = x.Name
                })
                .OrderBy(x => x.Name)
                .ToListAsync();

            return Results.Ok(tenants);
        });
    }
}
