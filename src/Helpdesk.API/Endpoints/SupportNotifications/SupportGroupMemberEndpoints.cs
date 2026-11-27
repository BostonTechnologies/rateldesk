using System.Security.Claims;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.SupportNotifications;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.SupportNotifications;

public static class SupportGroupMemberEndpoints
{
    public static void MapSupportGroupMemberEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/support/groups/{groupId}/members")
            .WithTags("Support Group Members")
            .RequireAuthorization("HelpdeskAdmin");

        group.MapGet("/", async (string groupId, [FromServices] IRepository<SupportGroupMember> repo, [FromServices] IRepository<User> users) =>
        {
            var userLookup = (await users.GetAllAsync()).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            var items = (await repo.GetAllAsync())
                .Where(x => string.Equals(x.SupportGroupId, groupId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.UserId)
                .Select(x => ToDto(x, userLookup))
                .ToList();
            return Results.Ok(items);
        });

        group.MapPost("/", async (string groupId, CreateSupportGroupMemberDto dto, HelpdeskDbContext db, ClaimsPrincipal actor, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(dto.UserId) || !Enum.IsDefined(dto.Role) || !Enum.IsDefined(dto.Source))
                return Results.BadRequest("Choose a valid user, membership role and source.");
            if (!await db.SupportGroups.AnyAsync(group => group.Id == groupId, ct))
                return Results.NotFound();
            var userId = dto.UserId.Trim();
            if (!await IsActiveUserAsync(db, userId, ct))
                return Results.BadRequest("Choose an active user.");
            if (await db.SupportGroupMembers.AnyAsync(member => member.SupportGroupId == groupId && member.UserId == userId, ct))
                return Results.Conflict(new { message = "This user already has a membership in the support group." });

            var created = new SupportGroupMember
            {
                SupportGroupId = groupId,
                UserId = userId,
                Role = dto.Role,
                Source = dto.Source,
                IsEnabled = dto.IsEnabled
            };
            db.SupportGroupMembers.Add(created);
            SupportGroupEndpoints.RecordChange(db, actor, groupId, $"Added support group membership for '{userId}'.");
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/support/groups/{groupId}/members/{created.Id}", ToDto(created, new Dictionary<string, User>()));
        });

        group.MapPut("/{id}", async (string groupId, string id, UpdateSupportGroupMemberDto dto, HelpdeskDbContext db, ClaimsPrincipal actor, CancellationToken ct) =>
        {
            var existing = await db.SupportGroupMembers.SingleOrDefaultAsync(member => member.Id == id && member.SupportGroupId == groupId, ct);
            if (existing is null) return Results.NotFound();
            if (!Enum.IsDefined(dto.Role) || !Enum.IsDefined(dto.Source))
                return Results.BadRequest("Choose a valid membership role and source.");
            if (dto.IsEnabled && !await IsActiveUserAsync(db, existing.UserId, ct))
                return Results.BadRequest("Choose an active user.");

            existing.Role = dto.Role;
            existing.Source = dto.Source;
            existing.IsEnabled = dto.IsEnabled;
            existing.UpdatedUtc = DateTimeOffset.UtcNow;
            SupportGroupEndpoints.RecordChange(db, actor, groupId, $"Updated support group membership for '{existing.UserId}'. Enabled: {dto.IsEnabled}.");
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(existing, new Dictionary<string, User>()));
        });

        group.MapDelete("/{id}", async (string groupId, string id, HelpdeskDbContext db, ClaimsPrincipal actor, CancellationToken ct) =>
        {
            var existing = await db.SupportGroupMembers.SingleOrDefaultAsync(member => member.Id == id && member.SupportGroupId == groupId, ct);
            if (existing is null) return Results.NotFound();
            db.SupportGroupMembers.Remove(existing);
            SupportGroupEndpoints.RecordChange(db, actor, groupId, $"Removed support group membership for '{existing.UserId}'.");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static Task<bool> IsActiveUserAsync(HelpdeskDbContext db, string userId, CancellationToken ct) =>
        db.Users.AnyAsync(user => user.Id == userId && !user.IsDeleted &&
            (user.OrganizationId == null || db.Organizations.Any(organization => organization.Id == user.OrganizationId && organization.IsEnabled)), ct);

    private static SupportGroupMemberDto ToDto(SupportGroupMember source, IReadOnlyDictionary<string, User> users)
    {
        users.TryGetValue(source.UserId, out var user);
        return new SupportGroupMemberDto
        {
            Id = source.Id,
            SupportGroupId = source.SupportGroupId,
            UserId = source.UserId,
            UserName = user?.Name,
            UserEmail = user?.Email,
            Role = source.Role,
            Source = source.Source,
            IsEnabled = source.IsEnabled,
            CreatedUtc = source.CreatedUtc,
            UpdatedUtc = source.UpdatedUtc
        };
    }
}
