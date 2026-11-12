using Helpdesk.Infrastructure.Auth.Rbac;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Helpdesk.API.Endpoints.Authentication;

public static class RoleDefinitionEndpoints
{
    public static void MapRoleDefinitionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/role-definitions")
            .WithTags("Role definitions")
            .RequireAuthorization();

        group.MapGet("/", async (
            HttpContext context,
            ICurrentUserAccessService accessService,
            HelpdeskDbContext db,
            CancellationToken cancellationToken) =>
        {
            var access = await accessService.ResolveAsync(context.User, cancellationToken);
            var organizationIds = ManagedOrganizationIds(access);
            if (!access.IsHelpdeskAdmin && organizationIds.Count == 0) return Results.Forbid();

            await RoleDefinitionSeeder.EnsureBuiltInsAsync(db, cancellationToken);
            var rolesQuery = db.Roles.AsNoTracking()
                .Include(role => role.Permissions)
                .OrderByDescending(role => role.IsBuiltIn)
                .ThenBy(role => role.Name)
                .AsQueryable();
            if (!access.IsHelpdeskAdmin)
            {
                rolesQuery = rolesQuery.Where(role => role.IsBuiltIn ||
                    (role.OwnerOrganizationId != null && organizationIds.Contains(role.OwnerOrganizationId)));
            }

            var roles = await rolesQuery.ToListAsync(cancellationToken);
            var roleKeys = roles.Select(role => role.Key).ToArray();
            var assignmentsQuery = db.ScopedRoleAssignments.AsNoTracking()
                .Where(assignment => roleKeys.Contains(assignment.RoleKey));
            if (!access.IsHelpdeskAdmin)
            {
                assignmentsQuery = assignmentsQuery.Where(assignment => organizationIds.Contains(assignment.OrganizationId));
            }

            var assignments = await assignmentsQuery
                .GroupBy(assignment => assignment.RoleKey)
                .Select(group => new { Key = group.Key, Count = group.Count() })
                .ToDictionaryAsync(item => item.Key, item => item.Count, StringComparer.OrdinalIgnoreCase, cancellationToken);
            return Results.Ok(roles.Select(role => ToResponse(role, assignments.GetValueOrDefault(role.Key))));
        });

        group.MapGet("/{id}", async (
            string id,
            HttpContext context,
            ICurrentUserAccessService accessService,
            HelpdeskDbContext db,
            CancellationToken cancellationToken) =>
        {
            var access = await accessService.ResolveAsync(context.User, cancellationToken);
            var role = await db.Roles.AsNoTracking()
                .Include(candidate => candidate.Permissions)
                .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
            if (role is null) return Results.NotFound();
            if (!CanManageOrganization(access, role.OwnerOrganizationId) && !role.IsBuiltIn) return Results.Forbid();
            if (!access.IsHelpdeskAdmin && ManagedOrganizationIds(access).Count == 0) return Results.Forbid();

            var assignments = db.ScopedRoleAssignments.AsNoTracking()
                .Where(assignment => assignment.RoleKey == role.Key);
            if (!access.IsHelpdeskAdmin)
            {
                var organizationIds = ManagedOrganizationIds(access);
                assignments = assignments.Where(assignment => organizationIds.Contains(assignment.OrganizationId));
            }

            return Results.Ok(ToResponse(role, await assignments.CountAsync(cancellationToken)));
        });

        group.MapGet("/{id}/assignments", async (
            string id,
            bool? paged,
            int? page,
            int? pageSize,
            HttpContext context,
            ICurrentUserAccessService accessService,
            HelpdeskDbContext db,
            CancellationToken cancellationToken) =>
        {
            var access = await accessService.ResolveAsync(context.User, cancellationToken);
            var role = await db.Roles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
            if (role is null) return Results.NotFound();
            if (!access.IsHelpdeskAdmin && ManagedOrganizationIds(access).Count == 0) return Results.Forbid();
            if (!role.IsBuiltIn && !CanManageOrganization(access, role.OwnerOrganizationId)) return Results.Forbid();

            var assignments = db.ScopedRoleAssignments.AsNoTracking().Where(assignment => assignment.RoleKey == role.Key);
            if (!access.IsHelpdeskAdmin)
            {
                var organizationIds = ManagedOrganizationIds(access);
                assignments = assignments.Where(assignment => organizationIds.Contains(assignment.OrganizationId));
            }

            var query = (
                from assignment in assignments
                join user in db.Users.AsNoTracking() on assignment.UserId equals user.Id
                orderby user.Name, assignment.OrganizationId, user.Id
                select new RoleAssignmentResponse(user.Id, user.Name, user.Email, assignment.OrganizationId)
            );
            if (paged == true)
            {
                var size = Math.Clamp(pageSize ?? 25, 1, 100);
                var total = await query.CountAsync(cancellationToken);
                var current = Math.Clamp(page ?? 1, 1, Math.Max(1, (int)Math.Ceiling((double)total / size)));
                return Results.Ok(new Helpdesk.Shared.DTOs.PagedResponse<RoleAssignmentResponse>
                {
                    Page = current, PageSize = size, TotalCount = total,
                    Items = await query.Skip((current - 1) * size).Take(size).ToListAsync(cancellationToken)
                });
            }
            return Results.Ok(await query.Take(200).ToArrayAsync(cancellationToken));
        });

        group.MapPost("/", async (
            CreateRoleDefinitionRequest request,
            HttpContext context,
            ICurrentUserAccessService accessService,
            HelpdeskDbContext db,
            CancellationToken cancellationToken) =>
        {
            var access = await accessService.ResolveAsync(context.User, cancellationToken);
            if (!CanManageOrganization(access, request.OwnerOrganizationId)) return Results.Forbid();

            await RoleDefinitionSeeder.EnsureBuiltInsAsync(db, cancellationToken);
            var validation = await ValidateCustomRoleAsync(request.Name, request.Key, request.OwnerOrganizationId, request.Permissions, null, db, cancellationToken);
            if (validation.Error is not null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [validation.Error.Key] = [validation.Error.Message] });
            }
            if (!access.IsHelpdeskAdmin && !RoleDefinitionCatalog.IsWithinTenantAdministratorPermissionCeiling(validation.Permissions!))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["permissions"] = ["Tenant administrators may delegate only approved operational permissions."]
                });
            }

            var role = new Role
            {
                Key = validation.Key!,
                Name = request.Name.Trim(),
                Scope = RoleScopeKind.Tenant,
                OwnerOrganizationId = request.OwnerOrganizationId!.Trim(),
                IsBuiltIn = false,
                IsProtected = false,
                Permissions = validation.Permissions!.Select(permission => new RolePermission { Permission = permission }).ToList()
            };
            db.Roles.Add(role);
            AddAuditLog(db, context.User, role,
                $"Created custom role '{role.Name}' in organization '{role.OwnerOrganizationId}'. Permissions: [{FormatPermissions(role.Permissions.Select(permission => permission.Permission))}].");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/v1/admin/role-definitions/{role.Id}", ToResponse(role, 0));
        });

        group.MapPut("/{id}", async (
            string id,
            UpdateRoleDefinitionRequest request,
            HttpContext context,
            ICurrentUserAccessService accessService,
            HelpdeskDbContext db,
            CancellationToken cancellationToken) =>
        {
            var access = await accessService.ResolveAsync(context.User, cancellationToken);
            await RoleDefinitionSeeder.EnsureBuiltInsAsync(db, cancellationToken);
            var role = await db.Roles.Include(candidate => candidate.Permissions)
                .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
            if (role is null) return Results.NotFound();
            if (!CanManageOrganization(access, role.OwnerOrganizationId)) return Results.Forbid();
            if (role.IsProtected || role.IsBuiltIn)
            {
                return Results.Conflict(new { message = "Built-in roles are protected and cannot be edited." });
            }

            var validation = await ValidateCustomRoleAsync(request.Name, role.Key, role.OwnerOrganizationId, request.Permissions, role.Id, db, cancellationToken);
            if (validation.Error is not null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [validation.Error.Key] = [validation.Error.Message] });
            }
            if (!access.IsHelpdeskAdmin && !RoleDefinitionCatalog.IsWithinTenantAdministratorPermissionCeiling(validation.Permissions!))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["permissions"] = ["Tenant administrators may delegate only approved operational permissions."]
                });
            }

            var previousName = role.Name;
            var previousPermissions = role.Permissions.Select(permission => permission.Permission).ToArray();
            role.Name = request.Name.Trim();
            db.RolePermissions.RemoveRange(role.Permissions);
            role.Permissions = validation.Permissions!.Select(permission => new RolePermission { RoleId = role.Id, Permission = permission }).ToList();
            AddAuditLog(db, context.User, role,
                $"Updated custom role '{previousName}' to '{role.Name}' in organization '{role.OwnerOrganizationId}'. Permissions: [{FormatPermissions(previousPermissions)}] -> [{FormatPermissions(validation.Permissions)}].");
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(ToResponse(role, await db.ScopedRoleAssignments.CountAsync(assignment => assignment.RoleKey == role.Key, cancellationToken)));
        });

        group.MapDelete("/{id}", async (
            string id,
            HttpContext context,
            ICurrentUserAccessService accessService,
            HelpdeskDbContext db,
            CancellationToken cancellationToken) =>
        {
            var access = await accessService.ResolveAsync(context.User, cancellationToken);
            var role = await db.Roles.Include(candidate => candidate.Permissions)
                .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
            if (role is null) return Results.NotFound();
            if (!CanManageOrganization(access, role.OwnerOrganizationId)) return Results.Forbid();
            if (role.IsProtected || role.IsBuiltIn)
            {
                return Results.Conflict(new { message = "Built-in roles are protected and cannot be deleted." });
            }
            if (await db.ScopedRoleAssignments.AnyAsync(assignment => assignment.RoleKey == role.Key, cancellationToken))
            {
                return Results.Conflict(new { message = "Remove the role from all assignments before deleting it." });
            }

            AddAuditLog(db, context.User, role,
                $"Deleted custom role '{role.Name}' in organization '{role.OwnerOrganizationId}'. Permissions: [{FormatPermissions(role.Permissions.Select(permission => permission.Permission))}].");
            db.Roles.Remove(role);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });
    }

    private static IReadOnlySet<string> ManagedOrganizationIds(CurrentUserAccessProfile access) =>
        access.OrganizationIdsFor(HelpdeskPermissions.TenantRolesAssign)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool CanManageOrganization(CurrentUserAccessProfile access, string? organizationId) =>
        access.IsHelpdeskAdmin ||
        (!string.IsNullOrWhiteSpace(organizationId) &&
         access.HasPermission(HelpdeskPermissions.TenantRolesAssign, organizationId));

    private static void AddAuditLog(HelpdeskDbContext db, ClaimsPrincipal user, Role role, string message) =>
        db.ActivityLogs.Add(new ActivityLog
        {
            UserId = ResolveActorId(user),
            RelatedEntityId = role.Id,
            Message = message
        });

    private static string ResolveActorId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ??
        user.FindFirstValue("sub") ??
        user.Identity?.Name ??
        "unknown";

    private static string FormatPermissions(IEnumerable<string> permissions) =>
        string.Join(", ", permissions.OrderBy(permission => permission, StringComparer.Ordinal));

    private static async Task<RoleValidation> ValidateCustomRoleAsync(
        string? name,
        string? key,
        string? ownerOrganizationId,
        IReadOnlyList<string>? requestedPermissions,
        string? existingRoleId,
        HelpdeskDbContext db,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) return RoleValidation.Invalid("name", "A role name is required.");
        if (string.IsNullOrWhiteSpace(ownerOrganizationId) ||
            !await db.Organizations.AnyAsync(organization => organization.Id == ownerOrganizationId && organization.IsEnabled, cancellationToken))
        {
            return RoleValidation.Invalid("ownerOrganizationId", "A custom role must be owned by an enabled organization.");
        }

        var roleKey = string.IsNullOrWhiteSpace(key) ? $"custom.{Slug(name)}" : key.Trim();
        if (!roleKey.StartsWith("custom.", StringComparison.OrdinalIgnoreCase) ||
            roleKey.Length > 128 ||
            roleKey.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
        {
            return RoleValidation.Invalid("key", "Custom role keys must start with 'custom.' and contain only letters, numbers, periods, or hyphens.");
        }

        var permissions = RoleDefinitionCatalog.NormalizePermissions(requestedPermissions);
        if (permissions.Count == 0 || permissions.Any(permission => !RoleDefinitionCatalog.IsAssignablePermission(permission)))
        {
            return RoleValidation.Invalid("permissions", "Choose one or more supported application permissions.");
        }
        if (!RoleDefinitionCatalog.SatisfiesDependencies(permissions))
        {
            return RoleValidation.Invalid("permissions", "Write, delete and approval permissions must include their corresponding read permission.");
        }

        var existing = await db.Roles.AsNoTracking()
            .AnyAsync(role => role.Key == roleKey && role.Id != existingRoleId, cancellationToken);
        if (existing)
        {
            return RoleValidation.Invalid("key", "A role with that key already exists.");
        }

        return new RoleValidation(roleKey, permissions, null);
    }

    private static string Slug(string value) => new(
        string.Join('-', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-').ToArray());

    private static RoleDefinitionResponse ToResponse(Role role, int assignmentCount) => new(
        role.Id,
        role.Key,
        role.Name,
        role.Scope,
        role.OwnerOrganizationId,
        role.IsBuiltIn,
        role.IsProtected,
        role.Permissions.Select(permission => permission.Permission).OrderBy(permission => permission).ToArray(),
        assignmentCount);

    public sealed record CreateRoleDefinitionRequest(string Name, string? Key, string? OwnerOrganizationId, IReadOnlyList<string> Permissions);
    public sealed record UpdateRoleDefinitionRequest(string Name, IReadOnlyList<string> Permissions);
    public sealed record RoleDefinitionResponse(string Id, string Key, string Name, RoleScopeKind Scope, string? OwnerOrganizationId, bool IsBuiltIn, bool IsProtected, IReadOnlyList<string> Permissions, int AssignmentCount);
    public sealed record RoleAssignmentResponse(string UserId, string Name, string Email, string OrganizationId);

    private sealed record RoleValidation(string? Key, IReadOnlyList<string>? Permissions, ValidationError? Error)
    {
        public static RoleValidation Invalid(string key, string message) => new(null, null, new ValidationError(key, message));
    }

    private sealed record ValidationError(string Key, string Message);
}
