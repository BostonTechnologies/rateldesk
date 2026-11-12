using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Customer;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.DTOs.User;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Search;

public static class GlobalSearchLookupEndpoints
{
    public static void MapGlobalSearchLookupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/global-search")
            .WithTags("Global Search")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/users", async (
            [FromServices] HelpdeskDbContext db,
            [FromQuery] string? q,
            [FromQuery] int? pageSize,
            CancellationToken token) =>
        {
            var term = q?.Trim();
            var take = Math.Clamp(pageSize ?? 6, 1, 25);
            var query = db.Users.AsNoTracking().Where(user => !user.IsDeleted);

            if (!string.IsNullOrWhiteSpace(term))
            {
                var like = Like(term);
                query = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
                    ? query.Where(x => EF.Functions.ILike(x.Name, like) || EF.Functions.ILike(x.Email, like) || EF.Functions.ILike(x.Role, like))
                    : query.Where(x => EF.Functions.Like(x.Name, like) || EF.Functions.Like(x.Email, like) || EF.Functions.Like(x.Role, like));
            }

            var rows = await query
                .OrderBy(x => x.Name)
                .ThenBy(x => x.Email)
                .Take(take)
                .Select(x => new UserDto(x.Id, x.Name, x.Email, x.Role, x.IsTestUser, x.OrganizationId))
                .ToListAsync(token);

            return Results.Ok(ToPaged(rows, take));
        })
        .WithName("GlobalSearchUsers")
        .WithSummary("Search users for the global app search overlay.");

        group.MapGet("/customers", async (
            [FromServices] HelpdeskDbContext db,
            [FromQuery] string? q,
            [FromQuery] int? pageSize,
            CancellationToken token) =>
        {
            var term = q?.Trim();
            var take = Math.Clamp(pageSize ?? 6, 1, 25);
            var query =
                from customer in db.Customers.AsNoTracking()
                join organization in db.Organizations.AsNoTracking() on customer.OrganizationId equals organization.Id into organizations
                from organization in organizations.DefaultIfEmpty()
                select new
                {
                    Customer = customer,
                    OrganizationName = organization != null ? organization.Name : null
                };

            if (!string.IsNullOrWhiteSpace(term))
            {
                var like = Like(term);
                query = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
                    ? query.Where(x => EF.Functions.ILike(x.Customer.Name, like) || EF.Functions.ILike(x.Customer.Email, like) || (x.OrganizationName != null && EF.Functions.ILike(x.OrganizationName, like)))
                    : query.Where(x => EF.Functions.Like(x.Customer.Name, like) || EF.Functions.Like(x.Customer.Email, like) || (x.OrganizationName != null && EF.Functions.Like(x.OrganizationName, like)));
            }

            var rows = await query
                .OrderBy(x => x.Customer.Name)
                .ThenBy(x => x.Customer.Email)
                .Take(take)
                .Select(x => new CustomerDto
                {
                    Id = x.Customer.Id,
                    Name = x.Customer.Name,
                    Email = x.Customer.Email,
                    OrganizationId = x.Customer.OrganizationId,
                    OrganizationName = x.OrganizationName ?? string.Empty,
                    State = x.Customer.State
                })
                .ToListAsync(token);

            return Results.Ok(ToPaged(rows, take));
        })
        .WithName("GlobalSearchCustomers")
        .WithSummary("Search customers for the global app search overlay.");

        group.MapGet("/organizations", async (
            [FromServices] HelpdeskDbContext db,
            [FromQuery] string? q,
            [FromQuery] int? pageSize,
            CancellationToken token) =>
        {
            var term = q?.Trim();
            var take = Math.Clamp(pageSize ?? 6, 1, 25);
            var query = db.Organizations.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(term))
            {
                var like = Like(term);
                query = db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL"
                    ? query.Where(x => EF.Functions.ILike(x.Name, like) || (x.DnsName != null && EF.Functions.ILike(x.DnsName, like)) || (x.ContactInfo != null && EF.Functions.ILike(x.ContactInfo, like)))
                    : query.Where(x => EF.Functions.Like(x.Name, like) || (x.DnsName != null && EF.Functions.Like(x.DnsName, like)) || (x.ContactInfo != null && EF.Functions.Like(x.ContactInfo, like)));
            }

            var rows = await query
                .OrderBy(x => x.Name)
                .Take(take)
                .Select(x => new OrganizationDto
                {
                    Id = x.Id,
                    Name = x.Name,
                    DnsName = x.DnsName,
                    ContactInfo = x.ContactInfo,
                    ItSupportOrganizationId = x.ItSupportOrganizationId,
                    OrchestrationTenantId = x.OrchestrationTenantId,
                    OrchestrationTenantName = x.OrchestrationTenantName,
                    OrchestrationTenantLinkedAtUtc = x.OrchestrationTenantLinkedAtUtc,
                    State = x.State
                })
                .ToListAsync(token);

            return Results.Ok(ToPaged(rows, take));
        })
        .WithName("GlobalSearchOrganizations")
        .WithSummary("Search organizations for the global app search overlay.");
    }

    private static PagedResponse<T> ToPaged<T>(List<T> rows, int pageSize) => new()
    {
        Page = 1,
        PageSize = pageSize,
        TotalCount = rows.Count,
        Items = rows
    };

    private static string Like(string term) => $"%{term}%";
}
