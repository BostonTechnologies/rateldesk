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
