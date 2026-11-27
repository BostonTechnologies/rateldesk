using System.Security.Claims;
using Helpdesk.API.Endpoints.Ticketing;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.DTOs.SupportNotifications;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.SupportNotifications;

public static class SupportGroupMemberEndpoints
{
    public static void MapSupportGroupMemberEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/support/groups/{groupId}/members")
            .WithTags("Support Group Members")
            .RequireAuthorization()
            .WithMetadata(new QueueAwareEndpoint())
            .AddEndpointFilter(SupportGroupEndpoints.FenceMutationAsync);

        group.MapGet("/", async (string groupId, HelpdeskDbContext db, ClaimsPrincipal actor,
            ICurrentUserAccessService accessService, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(actor, ct);
            if (!SupportGroupEndpoints.CanManageGroups(access, actor)) return Results.Forbid();
            if (!await SupportGroupEndpoints.ManagedGroups(db, access).AnyAsync(candidate => candidate.Id == groupId, ct))
                return Results.NotFound();
            var memberships = await (from member in db.SupportGroupMembers.AsNoTracking()
                join user in db.Users.AsNoTracking() on member.UserId equals user.Id into users
                from user in users.DefaultIfEmpty()
                where member.SupportGroupId == groupId
                orderby member.UserId
                select new { Member = member, User = user }).ToListAsync(ct);
            return Results.Ok(memberships.Select(row => ToDto(row.Member, row.User)));
        });

        group.MapPost("/", async (string groupId, CreateSupportGroupMemberDto dto, HelpdeskDbContext db, ClaimsPrincipal actor,
            ICurrentUserAccessService accessService, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(actor, ct);
            if (!SupportGroupEndpoints.CanManageGroups(access, actor)) return Results.Forbid();
            var owner = await SupportGroupEndpoints.ManagedGroups(db, access).Where(candidate => candidate.Id == groupId)
                .Select(candidate => candidate.OwningOrganizationId).SingleOrDefaultAsync(ct);
            if (owner is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(dto.UserId) || !Enum.IsDefined(dto.Role) || !Enum.IsDefined(dto.Source))
                return Results.BadRequest("Choose a valid user, membership role and source.");
            var userId = dto.UserId.Trim();
            if (!access.IsHelpdeskAdmin && (userId == access.DomainUserId || dto.Source != SupportGroupMemberSource.Manual))
                return Results.Forbid();
            if (!await IsActiveUserAsync(db, userId, access.IsHelpdeskAdmin ? null : owner, ct))
                return Results.BadRequest("Choose an active user authorized in the owning organization.");
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
            return Results.Created($"/api/v1/support/groups/{groupId}/members/{created.Id}", ToDto(created));
        });

        group.MapPut("/{id}", async (string groupId, string id, UpdateSupportGroupMemberDto dto, HelpdeskDbContext db, ClaimsPrincipal actor,
            ICurrentUserAccessService accessService, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(actor, ct);
            if (!SupportGroupEndpoints.CanManageGroups(access, actor)) return Results.Forbid();
            var owner = await SupportGroupEndpoints.ManagedGroups(db, access).Where(candidate => candidate.Id == groupId)
                .Select(candidate => candidate.OwningOrganizationId).SingleOrDefaultAsync(ct);
            if (owner is null) return Results.NotFound();
            var existing = await db.SupportGroupMembers.SingleOrDefaultAsync(member => member.Id == id && member.SupportGroupId == groupId, ct);
            if (existing is null) return Results.NotFound();
            if (!Enum.IsDefined(dto.Role) || !Enum.IsDefined(dto.Source))
                return Results.BadRequest("Choose a valid membership role and source.");
            if (!access.IsHelpdeskAdmin && (existing.Source != dto.Source || !existing.IsEnabled && dto.IsEnabled && existing.UserId == access.DomainUserId))
                return Results.Forbid();
            if (dto.IsEnabled && !await IsActiveUserAsync(db, existing.UserId, access.IsHelpdeskAdmin ? null : owner, ct))
                return Results.BadRequest("Choose an active user authorized in the owning organization.");

            existing.Role = dto.Role;
            existing.Source = dto.Source;
            existing.IsEnabled = dto.IsEnabled;
            existing.UpdatedUtc = DateTimeOffset.UtcNow;
            SupportGroupEndpoints.RecordChange(db, actor, groupId, $"Updated support group membership for '{existing.UserId}'. Enabled: {dto.IsEnabled}.");
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(existing));
        });

        group.MapDelete("/{id}", async (string groupId, string id, HelpdeskDbContext db, ClaimsPrincipal actor,
            ICurrentUserAccessService accessService, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(actor, ct);
            if (!SupportGroupEndpoints.CanManageGroups(access, actor)) return Results.Forbid();
            if (!await SupportGroupEndpoints.ManagedGroups(db, access).AnyAsync(candidate => candidate.Id == groupId, ct))
                return Results.NotFound();
            var existing = await db.SupportGroupMembers.SingleOrDefaultAsync(member => member.Id == id && member.SupportGroupId == groupId, ct);
            if (existing is null) return Results.NotFound();
            db.SupportGroupMembers.Remove(existing);
            SupportGroupEndpoints.RecordChange(db, actor, groupId, $"Removed support group membership for '{existing.UserId}'.");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static Task<bool> IsActiveUserAsync(HelpdeskDbContext db, string userId, string? owner, CancellationToken ct) =>
        db.Users.AnyAsync(user => user.Id == userId && !user.IsDeleted &&
            (owner == null || user.OrganizationId == owner || db.ScopedRoleAssignments.Any(assignment =>
                assignment.UserId == user.Id && assignment.OrganizationId == owner)) &&
            (user.OrganizationId == null || db.Organizations.Any(organization => organization.Id == user.OrganizationId && organization.IsEnabled)), ct);

    private static SupportGroupMemberDto ToDto(SupportGroupMember source, User? user = null)
    {
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
