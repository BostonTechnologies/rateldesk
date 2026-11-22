using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.Change;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Organization;

public static class OrganizationChangeParticipantEndpoints
{
    public static void MapOrganizationChangeParticipantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/organizations")
            .WithTags("Organizations")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/{id}/change-participants", async (
            [FromRoute] string id,
            [FromQuery] bool? includeImplementors,
            [FromServices] HelpdeskDbContext db,
            CancellationToken token) =>
        {
            var organization = await db.Organizations
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id, token);
            if (organization is null)
            {
                return Results.NotFound("Organization not found.");
            }

            var implementorOrganizationId = string.IsNullOrWhiteSpace(organization.ItSupportOrganizationId)
                ? organization.Id
                : organization.ItSupportOrganizationId;

            var users = await db.Users
                .AsNoTracking()
                .Where(x => x.OrganizationId == organization.Id)
                .OrderBy(x => x.Name)
                .ThenBy(x => x.Email)
                .Select(x => new ChangeParticipantUserDto(x.Id, x.Name, x.Email, x.Role, x.OrganizationId))
                .ToListAsync(token);

            var customerContacts = await db.Customers
                .AsNoTracking()
                .Where(x => x.OrganizationId == organization.Id && x.State == Helpdesk.Shared.Models.EntityState.Enabled)
                .OrderBy(x => x.Name)
                .ThenBy(x => x.Email)
                .Select(x => new ChangeParticipantUserDto(x.Id, x.Name, x.Email, "Customer", x.OrganizationId))
                .ToListAsync(token);

            var requesterApproverUsers = users
                .Where(x => string.Equals(x.OrganizationId, organization.Id, StringComparison.OrdinalIgnoreCase))
                .Concat(customerContacts)
                .DistinctBy(x => x.Id)
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<ChangeParticipantUserDto> implementorUsers = includeImplementors == false ? [] :
                await AssigneeQueries.Eligible(db, "change", organization)
                    .OrderBy(x => x.Name).ThenBy(x => x.Email).ThenBy(x => x.Id)
                    .Select(x => new ChangeParticipantUserDto(x.Id, x.Name, x.Email, x.Role, x.OrganizationId))
                    .ToListAsync(token);

            return Results.Ok(new ChangeParticipantsDto
            {
                OrganizationId = organization.Id,
                ItSupportOrganizationId = implementorOrganizationId,
                RequestedForUsers = requesterApproverUsers,
                ApproverUsers = requesterApproverUsers,
                ImplementorUsers = implementorUsers
            });
        })
        .WithName("GetOrganizationChangeParticipants")
        .WithSummary("Get users available for change-management participant fields.");
    }
}
