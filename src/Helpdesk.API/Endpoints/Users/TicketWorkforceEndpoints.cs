using System.Data;
using System.Security.Claims;
using Helpdesk.API.Endpoints.Ticketing;
using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Enums;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Users;

public static class TicketWorkforceEndpoints
{
    public static void MapTicketWorkforceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/users/{userId}/ticket-workforce/{organizationId}")
            .RequireAuthorization()
            .WithTags("Role definitions")
            .WithMetadata(new QueueAwareEndpoint());
        group.MapGet("/", GetAsync);
        group.MapPut("/", SetAsync);
    }

    private static async Task<IResult> GetAsync(
        string userId, string organizationId, ClaimsPrincipal principal, ICurrentUserAccessService accessService,
        HelpdeskDbContext db, CancellationToken ct)
    {
        var access = await accessService.ResolveAsync(principal, ct);
        if (!access.HasPermission(HelpdeskPermissions.TenantRolesAssign, organizationId)) return Results.Forbid();
        if (!await IsInOrganizationAsync(db, userId, organizationId, ct)) return Results.NotFound();

        var roles = await AssignedRoles(db, userId, organizationId).AsNoTracking()
            .Include(role => role.Permissions).ToListAsync(ct);
        var isRestricted = roles.Any(role => role.TicketAccessScope != TicketAccessScope.AllAuthorizedQueues) ||
            await db.TicketWorkforceRestrictions.AnyAsync(row => row.UserId == userId && row.OrganizationId == organizationId, ct);
        var conflictingRoles = isRestricted ? roles
            .Where(role => role.TicketAccessScope == TicketAccessScope.AllAuthorizedQueues &&
                role.Permissions.Any(permission => RoleDefinitionCatalog.IsWorkforceTicketPermission(permission.Permission)))
            .Select(role => role.Key).Order(StringComparer.Ordinal).ToArray() : [];
        return Results.Ok(new TicketWorkforceStatus(isRestricted, conflictingRoles));
    }

    private static async Task<IResult> SetAsync(
        string userId, string organizationId, SetTicketWorkforceRequest request, ClaimsPrincipal principal,
        ICurrentUserAccessService accessService, HelpdeskDbContext db, CancellationToken ct)
    {
        var access = await accessService.ResolveAsync(principal, ct);
        var actorId = access.DomainUserId ?? principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (!access.HasPermission(HelpdeskPermissions.TenantRolesAssign, organizationId) || actorId == null ||
            string.Equals(actorId, userId, StringComparison.OrdinalIgnoreCase) || principal.HasClaim("auth_mode", "integration"))
            return Results.Forbid();
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 1024)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["reason"] = ["Enter a reason of up to 1024 characters."] });

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        if (!await IsInOrganizationAsync(db, userId, organizationId, ct)) return Results.NotFound();
        if (!request.Restricted && await AssignedRoles(db, userId, organizationId)
                .AnyAsync(role => role.TicketAccessScope != TicketAccessScope.AllAuthorizedQueues, ct))
            return Results.Conflict(new { message = "Remove group-scoped role assignments before switching to unrestricted access." });

        var restriction = await db.TicketWorkforceRestrictions.SingleOrDefaultAsync(
            row => row.UserId == userId && row.OrganizationId == organizationId, ct);
        if (request.Restricted == (restriction != null)) return Results.NoContent();
        if (request.Restricted)
            await TicketWorkforceRestrictions.EnsureAsync(db, organizationId, [userId], actorId, ct);
        else if (restriction != null)
            db.TicketWorkforceRestrictions.Remove(restriction);
        db.ActivityLogs.Add(new ActivityLog
        {
            RelatedEntityId = userId,
            UserId = actorId,
            Message = $"Changed ticket workforce access in organization '{organizationId}' to {(request.Restricted ? "restricted" : "unrestricted")}. {request.Reason.Trim()}"
        });
        await db.SaveChangesAsync(ct);
        if (transaction != null) await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static IQueryable<Role> AssignedRoles(HelpdeskDbContext db, string userId, string organizationId) =>
        from assignment in db.ScopedRoleAssignments
        join role in db.Roles on assignment.RoleKey equals role.Key
        where assignment.UserId == userId && assignment.OrganizationId == organizationId
        select role;

    private static Task<bool> IsInOrganizationAsync(HelpdeskDbContext db, string userId, string organizationId, CancellationToken ct) =>
        db.Users.AnyAsync(user => user.Id == userId && !user.IsDeleted &&
            db.Organizations.Any(organization => organization.Id == organizationId && organization.IsEnabled) &&
            (user.OrganizationId == organizationId ||
             db.ScopedRoleAssignments.Any(row => row.UserId == user.Id && row.OrganizationId == organizationId) ||
             db.TicketWorkforceRestrictions.Any(row => row.UserId == user.Id && row.OrganizationId == organizationId)), ct);

    public sealed record TicketWorkforceStatus(bool Restricted, IReadOnlyList<string> ConflictingRoleKeys);
    public sealed record SetTicketWorkforceRequest(bool Restricted, string Reason);
}
