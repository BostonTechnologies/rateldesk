using System.Security.Claims;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.SupportNotifications;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.SupportNotifications;

public static class SupportGroupEndpoints
{
    public static void MapSupportGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/support/groups")
            .WithTags("Support Groups")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/", async ([FromServices] IRepository<SupportGroup> repo, string? owningOrganizationId = null) =>
        {
            var items = (await repo.GetAllAsync())
                .Where(x => string.IsNullOrWhiteSpace(owningOrganizationId) || string.Equals(x.OwningOrganizationId, owningOrganizationId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.OwningOrganizationId)
                .ThenBy(x => x.Name)
                .Select(ToDto)
                .ToList();
            return Results.Ok(items);
        });

        group.MapGet("/{id}", async (string id, [FromServices] IRepository<SupportGroup> repo) =>
            await repo.GetAsync(id) is { } item ? Results.Ok(ToDto(item)) : Results.NotFound());

        group.MapPost("/", async (CreateSupportGroupDto dto, HelpdeskDbContext db, CancellationToken ct) =>
        {
            if (await ValidateAsync(db, dto.OwningOrganizationId, dto.Name, dto.Description, ct) is { } error)
                return error;

            var created = new SupportGroup
            {
                OwningOrganizationId = dto.OwningOrganizationId.Trim(),
                Name = dto.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
                IsEnabled = dto.IsEnabled
            };
            db.SupportGroups.Add(created);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/support/groups/{created.Id}", ToDto(created));
        });

        group.MapPut("/{id}", async (string id, UpdateSupportGroupDto dto, HelpdeskDbContext db, ClaimsPrincipal actor, CancellationToken ct) =>
        {
            var existing = await db.SupportGroups.SingleOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (existing is null) return Results.NotFound();
            if (await ValidateAsync(db, dto.OwningOrganizationId, dto.Name, dto.Description, ct) is { } error)
                return error;

            var ownerId = dto.OwningOrganizationId.Trim();
            if (existing.OwningOrganizationId != ownerId &&
                (await IsReferencedAsync(db, id, ct) || await db.SupportGroupMembers.AnyAsync(member => member.SupportGroupId == id, ct)))
            {
                return Results.Conflict(new { message = "A support group with existing assignments, members or coverage must retain its owning organization." });
            }

            if (existing.IsEnabled != dto.IsEnabled)
                RecordChange(db, actor, id, dto.IsEnabled ? "Enabled support group." : "Disabled support group.");
            existing.OwningOrganizationId = ownerId;
            existing.Name = dto.Name.Trim();
            existing.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
            existing.IsEnabled = dto.IsEnabled;
            existing.UpdatedUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(existing));
        });

        group.MapDelete("/{id}", async (string id, HelpdeskDbContext db, ClaimsPrincipal actor, CancellationToken ct) =>
        {
            var existing = await db.SupportGroups.SingleOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (existing is null) return Results.NotFound();
            if (await IsReferencedAsync(db, id, ct))
                return Results.Conflict(new { message = "Remove ticket assignments and coverage before deleting this support group." });

            db.SupportGroups.Remove(existing);
            RecordChange(db, actor, id, "Deleted support group.");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static async Task<bool> IsReferencedAsync(HelpdeskDbContext db, string id, CancellationToken ct) =>
        await db.Tickets.IgnoreQueryFilters().AnyAsync(ticket => ticket.AssignedSupportGroupId == id, ct) ||
        await db.OrganizationSupportCoverages.AnyAsync(coverage => coverage.SupportGroupId == id, ct);

    private static async Task<IResult?> ValidateAsync(HelpdeskDbContext db, string organizationId, string name, string? description, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(organizationId) || string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200 ||
            description?.Trim().Length > 1024)
            return Results.BadRequest("Provide an owning organization, a group name up to 200 characters and a description up to 1024 characters.");

        return await db.Organizations.AnyAsync(organization => organization.Id == organizationId.Trim() && organization.IsEnabled, ct)
            ? null : Results.BadRequest("Choose an enabled owning organization.");
    }

    internal static void RecordChange(HelpdeskDbContext db, ClaimsPrincipal actor, string id, string message) =>
        db.ActivityLogs.Add(new ActivityLog
        {
            RelatedEntityId = id,
            UserId = actor.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor.FindFirstValue("sub") ?? "unknown",
            Message = message
        });

    private static SupportGroupDto ToDto(SupportGroup source) => new()
    {
        Id = source.Id,
        OwningOrganizationId = source.OwningOrganizationId,
        Name = source.Name,
        Description = source.Description,
        IsEnabled = source.IsEnabled,
        CreatedUtc = source.CreatedUtc,
        UpdatedUtc = source.UpdatedUtc
    };
}
