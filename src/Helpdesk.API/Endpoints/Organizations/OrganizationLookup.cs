using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs;
using Helpdesk.Shared.DTOs.Organization;
using Helpdesk.Shared.DTOs.AiOps;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Organizations;

internal static class OrganizationLookup
{
    internal static async Task<IResult> SearchAsync(HelpdeskDbContext db, IQueryable<Helpdesk.Shared.Models.Organization> eligible,
        string? q, string? id, int? limit, CancellationToken ct, bool namesOnly = false, bool administrativeDetails = false)
    {
        if (q?.Length > 200 || limit is < 1) return Results.BadRequest();
        var total = await eligible.CountAsync(ct);
        if (!string.IsNullOrWhiteSpace(id)) eligible = eligible.Where(x => x.Id == id);
        else if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToUpperInvariant();
            if (db.Database.IsSqlite())
            {
                SqliteUnicodeSearch.RegisterConnection(db);
                eligible = eligible.Where(x => SqliteUnicodeSearch.Fold(x.Name)!.Contains(term) ||
                    !namesOnly && (x.Id == q || x.DnsName != null && SqliteUnicodeSearch.Fold(x.DnsName)!.Contains(term)));
            }
            else eligible = eligible.Where(x => x.Name.ToUpper().Contains(term) ||
                !namesOnly && (x.Id == q || x.DnsName != null && x.DnsName.ToUpper().Contains(term)));
        }
        var take = Math.Min(limit ?? 20, 50);
        var ordered = eligible.OrderBy(x => x.Name.ToLower()).ThenBy(x => x.Id);
        if (namesOnly)
        {
            var names = await ordered.Select(x => new AiOpsOrganizationDto(x.Id, x.Name)).Take(take + 1).ToListAsync(ct);
            return Page(names, total, take);
        }
        var items = await ordered
            .Select(x => new OrganizationDto { Id = x.Id, Name = x.Name, DnsName = x.DnsName, State = x.State, ItSupportOrganizationId = administrativeDetails ? x.ItSupportOrganizationId : null })
            .Take(take + 1).ToListAsync(ct);
        return Page(items, total, take);
    }

    private static IResult Page<T>(List<T> items, int total, int take)
    {
        var more = items.Count > take;
        if (more) items.RemoveAt(take);
        return Results.Ok(new CreationLookupResult<T>(items, total, more));
    }
}
