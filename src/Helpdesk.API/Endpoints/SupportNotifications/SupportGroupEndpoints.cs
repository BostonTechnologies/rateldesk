using System.Data;
using System.Security.Claims;
using Helpdesk.API.Endpoints.Ticketing;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.SupportNotifications;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.SupportNotifications;

public static class SupportGroupEndpoints
{
    public static void MapSupportGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/support/groups")
            .WithTags("Support Groups")
            .RequireAuthorization()
            .WithMetadata(new QueueAwareEndpoint())
            .AddEndpointFilter(FenceMutationAsync);

        group.MapGet("/", async (HelpdeskDbContext db, ClaimsPrincipal actor, ICurrentUserAccessService accessService,
            string? owningOrganizationId, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(actor, ct);
            if (!CanManageGroups(access, actor)) return Results.Forbid();
            var groups = ManagedGroups(db, access).AsNoTracking();
            if (!string.IsNullOrWhiteSpace(owningOrganizationId))
            {
                var owner = owningOrganizationId.Trim().ToLowerInvariant();
                groups = groups.Where(item => item.OwningOrganizationId.ToLower() == owner);
            }
            var items = await groups
                .OrderBy(x => x.OwningOrganizationId)
                .ThenBy(x => x.Name)
                .ToListAsync(ct);
            return Results.Ok(items.Select(ToDto));
        });

        group.MapGet("/{id}", async (string id, HelpdeskDbContext db, ClaimsPrincipal actor,
            ICurrentUserAccessService accessService, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(actor, ct);
            if (!CanManageGroups(access, actor)) return Results.Forbid();
            var item = await ManagedGroups(db, access).AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id, ct);
            return item is null ? Results.NotFound() : Results.Ok(ToDto(item));
        });

        group.MapPost("/", async (CreateSupportGroupDto dto, HelpdeskDbContext db, ClaimsPrincipal actor,
            ICurrentUserAccessService accessService, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(actor, ct);
            if (!CanManageGroups(access, actor) || !access.HasPermission(HelpdeskPermissions.TenantSettingsManage, dto.OwningOrganizationId?.Trim()))
                return Results.Forbid();
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
            RecordChange(db, actor, created.Id, "Created support group.");
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/support/groups/{created.Id}", ToDto(created));
        });

        group.MapPut("/{id}", async (string id, UpdateSupportGroupDto dto, HelpdeskDbContext db, ClaimsPrincipal actor,
            ICurrentUserAccessService accessService, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(actor, ct);
            if (!CanManageGroups(access, actor)) return Results.Forbid();
            var existing = await ManagedGroups(db, access).SingleOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (existing is null) return Results.NotFound();
            if (!access.IsHelpdeskAdmin && existing.OwningOrganizationId != dto.OwningOrganizationId?.Trim()) return Results.Forbid();
            if (await ValidateAsync(db, dto.OwningOrganizationId, dto.Name, dto.Description, ct) is { } error)
                return error;

            var ownerId = dto.OwningOrganizationId.Trim();
            if (existing.OwningOrganizationId != ownerId &&
                (await IsReferencedAsync(db, id, ct) || await db.SupportGroupMembers.AnyAsync(member => member.SupportGroupId == id, ct)))
            {
                return Results.Conflict(new { message = "A support group with existing ticket, role, membership or coverage references must retain its owning organization." });
            }

            if (!existing.IsEnabled && dto.IsEnabled && !access.IsHelpdeskAdmin &&
                await db.SupportGroupMembers.AnyAsync(member => member.SupportGroupId == id && member.IsEnabled &&
                    member.UserId == access.DomainUserId, ct)) return Results.Forbid();
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

        group.MapDelete("/{id}", async (string id, HelpdeskDbContext db, ClaimsPrincipal actor,
            ICurrentUserAccessService accessService, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(actor, ct);
            if (!CanManageGroups(access, actor)) return Results.Forbid();
            var existing = await ManagedGroups(db, access).SingleOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (existing is null) return Results.NotFound();
            if (await IsReferencedAsync(db, id, ct))
                return Results.Conflict(new { message = "Remove ticket assignments, role references and coverage before deleting this support group." });

            db.SupportGroups.Remove(existing);
            RecordChange(db, actor, id, "Deleted support group.");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    internal static bool CanManageGroups(CurrentUserAccessProfile access, ClaimsPrincipal actor) =>
        access.IsHelpdeskAdmin || (!actor.HasClaim("auth_mode", "integration") &&
            access.OrganizationIdsFor(HelpdeskPermissions.TenantSettingsManage).Count > 0);

    internal static IQueryable<SupportGroup> ManagedGroups(HelpdeskDbContext db, CurrentUserAccessProfile access)
    {
        if (access.IsHelpdeskAdmin) return db.SupportGroups;
        var owners = access.OrganizationIdsFor(HelpdeskPermissions.TenantSettingsManage).ToArray();
        return db.SupportGroups.Where(group => owners.Contains(group.OwningOrganizationId) &&
            db.Organizations.Any(org => org.Id == group.OwningOrganizationId && org.IsEnabled));
    }

    internal static async ValueTask<object?> FenceMutationAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var http = invocation.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method)) return await next(invocation);
        var db = http.RequestServices.GetRequiredService<HelpdeskDbContext>();
        if (!db.Database.IsRelational()) return await next(invocation);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, http.RequestAborted);
            var result = await next(invocation);
            if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
                await transaction.CommitAsync(http.RequestAborted);
            return result;
        }
        catch (Exception error) when (TicketQueueEndpoints.IsConcurrentWrite(error) || TicketQueueEndpoints.IsConcurrentWrite(error.InnerException))
        {
            return Results.Conflict(new { message = "The support group or its authorization changed. Refresh and try again." });
        }
    }

    private static async Task<bool> IsReferencedAsync(HelpdeskDbContext db, string id, CancellationToken ct) =>
        await db.Tickets.IgnoreQueryFilters().AnyAsync(ticket => ticket.AssignedSupportGroupId == id, ct) ||
        await db.RoleSupportGroups.AnyAsync(link => link.SupportGroupId == id, ct) ||
        await db.IncidentReceiverQueueRoutes.AnyAsync(route => route.SupportGroupId == id, ct) ||
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
