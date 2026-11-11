using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Helpdesk.API.Endpoints.Users;

public static class TenantAdministrationEndpoints
{
    private static readonly HashSet<string> BuiltInDelegableRoleKeys =
        [ScopedRoleCatalog.SelfServiceUser, ScopedRoleCatalog.Technician,
         ScopedRoleCatalog.IncidentReader, ScopedRoleCatalog.IncidentWriter,
         ScopedRoleCatalog.RequestReader, ScopedRoleCatalog.RequestWriter, ScopedRoleCatalog.RequestExecutor,
         ScopedRoleCatalog.ChangeReader, ScopedRoleCatalog.ChangeWriter, ScopedRoleCatalog.ChangeApprover];

    public static void MapTenantAdministrationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/tenant-admin/organizations", async (
            string? permission,
            HttpContext context,
            ICurrentUserAccessService accessService,
            HelpdeskDbContext db,
            CancellationToken cancellationToken) =>
        {
            var access = await accessService.ResolveAsync(context.User, cancellationToken);
            var requiredPermission = ResolveOrganizationLookupPermission(permission);
            if (requiredPermission is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["permission"] = ["Choose a supported tenant administration permission."]
                });
            }
            var organizationIds = access.IsHelpdeskAdmin
                ? null
                : access.OrganizationIdsFor(requiredPermission);
            if (organizationIds is { Count: 0 })
            {
                return Results.Ok(Array.Empty<TenantOrganizationResponse>());
            }

            var organizations = db.Organizations.AsNoTracking().Where(organization => organization.IsEnabled);
            if (organizationIds is not null)
            {
                organizations = organizations.Where(organization => organizationIds.Contains(organization.Id));
            }

            return Results.Ok(await organizations
                .OrderBy(organization => organization.Name)
                .Select(organization => new TenantOrganizationResponse(organization.Id, organization.Name))
                .ToArrayAsync(cancellationToken));
        })
        .RequireAuthorization()
        .WithName("GetTenantAdministrationOrganizations")
        .WithTags("Tenant administration");

        var settings = app.MapGroup("/api/v1/tenant-admin/organizations/{organizationId}/settings")
            .WithTags("Tenant settings")
            .RequireAuthorization();
        settings.MapGet("/", async (string organizationId, HttpContext context,
            ICurrentUserAccessService accessService, HelpdeskDbContext db, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(context.User, ct);
            if (!access.HasPermission(HelpdeskPermissions.TenantSettingsManage, organizationId)) return Results.Forbid();
            var organization = await db.Organizations.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == organizationId && candidate.IsEnabled, ct);
            return organization is null ? Results.NotFound() : Results.Ok(new TenantSettingsResponse(
                organization.Id, organization.Name, organization.ContactInfo));
        });
        settings.MapPut("/", async (string organizationId, UpdateTenantSettingsRequest request,
            HttpContext context, ICurrentUserAccessService accessService, HelpdeskDbContext db, CancellationToken ct) =>
        {
            var access = await accessService.ResolveAsync(context.User, ct);
            if (!access.HasPermission(HelpdeskPermissions.TenantSettingsManage, organizationId)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200 || request.ContactInfo?.Length > 4000)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["settings"] = ["Organization name is required (up to 200 characters); contact information must not exceed 4000 characters."]
                });
            var organization = await db.Organizations
                .SingleOrDefaultAsync(candidate => candidate.Id == organizationId && candidate.IsEnabled, ct);
            if (organization is null) return Results.NotFound();
            organization.Name = request.Name.Trim();
            organization.ContactInfo = string.IsNullOrWhiteSpace(request.ContactInfo) ? null : request.ContactInfo.Trim();
            db.ActivityLogs.Add(new ActivityLog
            {
                UserId = ResolveActorId(context.User), RelatedEntityId = organizationId,
                Message = $"Updated organization display and contact settings in organization '{organizationId}'."
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new TenantSettingsResponse(organization.Id, organization.Name, organization.ContactInfo));
        });

        var group = app.MapGroup("/api/v1/tenant-admin/organizations/{organizationId}/users")
            .WithTags("Tenant administration")
            .RequireAuthorization();

        group.MapPost("/", async (
            string organizationId,
            CreateTenantLocalAccountRequest request,
            HttpContext context,
            ICurrentUserAccessService accessService,
            HelpdeskDbContext db,
            UserManager<ApplicationUser> users,
            CancellationToken cancellationToken) =>
        {
            if (!await CanInviteAsync(context, accessService, organizationId, cancellationToken)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.Email))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["account"] = ["Display name and email are required."]
                });
            }

            if (!await db.Organizations.AnyAsync(organization => organization.Id == organizationId && organization.IsEnabled, cancellationToken))
            {
                return Results.NotFound();
            }

            var email = request.Email.Trim();
            if (await db.Users.AsNoTracking().AnyAsync(user => user.Email == email, cancellationToken) ||
                await users.FindByEmailAsync(email) is not null)
            {
                return Results.Conflict(new { error = "local_account_already_exists" });
            }

            var account = new ApplicationUser
            {
                UserName = email,
                Email = email,
                DisplayName = request.DisplayName.Trim(),
                EmailConfirmed = false,
                IsInstanceAdministrator = false
            };
            var created = await users.CreateAsync(account);
            if (!created.Succeeded)
            {
                return Results.Conflict(new { error = "local_account_could_not_be_created" });
            }

            try
            {
                var linkError = await LocalCustomerAccessLinker.LinkAsync(
                    db,
                    account.Id,
                    account.DisplayName,
                    account.Email!,
                    organizationId,
                    cancellationToken);
                if (linkError is not null)
                {
                    await users.DeleteAsync(account);
                    return Results.Conflict(new { error = "local_customer_link_conflict", message = linkError });
                }

                db.Users.Add(new User
                {
                    Id = account.Id,
                    Name = account.DisplayName,
                    Email = account.Email!,
                    Role = "User",
                    OrganizationId = organizationId
                });
                db.ScopedRoleAssignments.Add(new ScopedRoleAssignment
                {
                    UserId = account.Id,
                    OrganizationId = organizationId,
                    RoleKey = ScopedRoleCatalog.SelfServiceUser
                });
                db.ActivityLogs.Add(new ActivityLog
                {
                    UserId = ResolveActorId(context.User),
                    RelatedEntityId = account.Id,
                    Message = $"Created a tenant-local self-service invitation in organization '{organizationId}'."
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception)
            {
                await users.DeleteAsync(account);
                return Results.Problem("The local account could not be linked to the tenant.", statusCode: StatusCodes.Status409Conflict);
            }

            var activationToken = await users.GeneratePasswordResetTokenAsync(account);
            return Results.Created(
                $"/api/v1/tenant-admin/organizations/{organizationId}/users/{account.Id}",
                new TenantLocalAccountInvitationResponse(account.Id, account.Email!, activationToken));
        });

        group.MapGet("/", async (
            string organizationId,
            HttpContext context,
            ICurrentUserAccessService accessService,
            HelpdeskDbContext db,
            UserManager<ApplicationUser> users,
            CancellationToken cancellationToken) =>
        {
            if (!await CanViewMembersAsync(context, accessService, organizationId, cancellationToken)) return Results.Forbid();

            var members = await db.Users.AsNoTracking().Where(user => !user.IsDeleted)
                .Where(user => user.OrganizationId == organizationId || db.ScopedRoleAssignments.Any(assignment =>
                    assignment.UserId == user.Id && assignment.OrganizationId == organizationId))
                .OrderBy(user => user.Name)
                .Select(user => new TenantMemberCandidate(user.Id, user.Name, user.Email))
                .ToArrayAsync(cancellationToken);
            var memberIds = members.Select(member => member.Id).ToArray();
            var localUserIds = await users.Users.AsNoTracking()
                .Where(user => memberIds.Contains(user.Id) && !user.IsInstanceAdministrator)
                .Select(user => user.Id)
                .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, cancellationToken);
            var delegableRoleKeys = await GetDelegableRoleKeysAsync(organizationId, db, cancellationToken);
            var assignments = await db.ScopedRoleAssignments.AsNoTracking()
                .Where(assignment =>
                    assignment.OrganizationId == organizationId &&
                    localUserIds.Contains(assignment.UserId) &&
                    delegableRoleKeys.Contains(assignment.RoleKey))
                .OrderBy(assignment => assignment.RoleKey)
                .ToArrayAsync(cancellationToken);
            var rolesByUser = assignments
                .GroupBy(assignment => assignment.UserId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group.Select(assignment => assignment.RoleKey).ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            return Results.Ok(members
                .Where(member => localUserIds.Contains(member.Id))
                .Select(member => new TenantMemberResponse(
                    member.Id,
                    member.Name,
                    member.Email,
                    rolesByUser.GetValueOrDefault(member.Id, [])))
                .ToArray());
        });

        group.MapGet("/roles", async (
            string organizationId,
            HttpContext context,
            ICurrentUserAccessService accessService,
            HelpdeskDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!await CanManageAsync(context, accessService, organizationId, cancellationToken)) return Results.Forbid();

            var roleKeys = await GetDelegableRoleKeysAsync(organizationId, db, cancellationToken);
            var customRoleNames = await db.Roles.AsNoTracking()
                .Where(role => roleKeys.Contains(role.Key))
                .ToDictionaryAsync(role => role.Key, role => role.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);

            return Results.Ok(roleKeys
                .Select(roleKey => new TenantDelegableRoleResponse(
                    roleKey,
                    customRoleNames.GetValueOrDefault(roleKey) ??
                    RoleDefinitionCatalog.Find(roleKey)?.Name ??
                    roleKey))
                .OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        });

        group.MapGet("/{userId}/assignments", async (
            string organizationId,
            string userId,
            HttpContext context,
            ICurrentUserAccessService accessService,
            HelpdeskDbContext db,
            UserManager<ApplicationUser> users,
            CancellationToken cancellationToken) =>
        {
            if (!await CanManageAsync(context, accessService, organizationId, cancellationToken)) return Results.Forbid();
            var target = await FindTenantLocalUserAsync(userId, organizationId, db, users, cancellationToken);
            if (target is null) return Results.NotFound();
            if (target.IsInstanceAdministrator) return Results.Forbid();

            var delegableRoleKeys = await GetDelegableRoleKeysAsync(organizationId, db, cancellationToken);
            var assignments = await db.ScopedRoleAssignments.AsNoTracking()
                .Where(assignment =>
                    assignment.UserId == userId &&
                    assignment.OrganizationId == organizationId &&
                    delegableRoleKeys.Contains(assignment.RoleKey))
                .OrderBy(assignment => assignment.RoleKey)
                .Select(assignment => assignment.RoleKey)
                .ToArrayAsync(cancellationToken);
            return Results.Ok(new TenantMembershipResponse(userId, organizationId, assignments));
        });

        group.MapPut("/{userId}/assignments", async (
            string organizationId,
            string userId,
            ReplaceTenantMembershipRequest request,
            HttpContext context,
            ICurrentUserAccessService accessService,
            HelpdeskDbContext db,
            UserManager<ApplicationUser> users,
            CancellationToken cancellationToken) =>
        {
            if (!await CanManageAsync(context, accessService, organizationId, cancellationToken)) return Results.Forbid();
            var target = await FindTenantLocalUserAsync(userId, organizationId, db, users, cancellationToken);
            if (target is null) return Results.NotFound();
            if (target.IsInstanceAdministrator) return Results.Forbid();

            var roleKeys = (request.RoleKeys ?? [])
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var delegableRoleKeys = await GetDelegableRoleKeysAsync(organizationId, db, cancellationToken);
            if (roleKeys.Any(key => !delegableRoleKeys.Contains(key)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["roleKeys"] = ["Tenant administrators may assign only operational built-ins or tenant-owned roles within the approved delegation ceiling."]
                });
            }

            roleKeys = roleKeys.Select(key => delegableRoleKeys.Single(known =>
                string.Equals(known, key, StringComparison.OrdinalIgnoreCase))).ToArray();
            var existing = await db.ScopedRoleAssignments
                .Where(assignment => assignment.UserId == userId && assignment.OrganizationId == organizationId)
                .ToListAsync(cancellationToken);
            var hasDelegableAccess = roleKeys.Length > 0;
            db.ScopedRoleAssignments.RemoveRange(existing.Where(assignment => delegableRoleKeys.Contains(assignment.RoleKey)));
            db.ScopedRoleAssignments.AddRange(roleKeys.Select(key => new ScopedRoleAssignment
            {
                UserId = userId,
                OrganizationId = organizationId,
                RoleKey = key
            }));
            var previousRoleKeys = existing.Where(assignment => delegableRoleKeys.Contains(assignment.RoleKey))
                .Select(assignment => assignment.RoleKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!previousRoleKeys.SetEquals(roleKeys))
            {
                var selfServiceOnly = (roleKeys.Length == 1 &&
                    string.Equals(roleKeys[0], ScopedRoleCatalog.SelfServiceUser, StringComparison.OrdinalIgnoreCase)) ||
                    (roleKeys.Length == 0 && existing
                        .Where(assignment => delegableRoleKeys.Contains(assignment.RoleKey))
                        .All(assignment => string.Equals(assignment.RoleKey, ScopedRoleCatalog.SelfServiceUser, StringComparison.OrdinalIgnoreCase)));
                var accessDescription = selfServiceOnly
                    ? "tenant self-service access"
                    : "delegated tenant access";
                db.ActivityLogs.Add(new ActivityLog
                {
                    UserId = ResolveActorId(context.User),
                    RelatedEntityId = userId,
                    Message = $"{(hasDelegableAccess ? "Granted" : "Removed")} {accessDescription} in organization '{organizationId}'. " +
                              $"Roles: [{string.Join(", ", previousRoleKeys.Order(StringComparer.OrdinalIgnoreCase))}] -> [{string.Join(", ", roleKeys.Order(StringComparer.OrdinalIgnoreCase))}]."
                });
            }
            await db.SaveChangesAsync(cancellationToken);

            target.AuthorizationRevision++;
            target.SecurityStamp = Guid.NewGuid().ToString("N");
            var update = await users.UpdateAsync(target);
            return update.Succeeded ? Results.NoContent() : Results.Conflict();
        });
    }

    private static async Task<HashSet<string>> GetDelegableRoleKeysAsync(
        string organizationId,
        HelpdeskDbContext db,
        CancellationToken cancellationToken)
    {
        var roleKeys = new HashSet<string>(BuiltInDelegableRoleKeys, StringComparer.OrdinalIgnoreCase);
        var customRoles = await db.Roles.AsNoTracking()
            .Include(role => role.Permissions)
            .Where(role =>
                !role.IsBuiltIn &&
                role.Scope == RoleScopeKind.Tenant &&
                role.OwnerOrganizationId == organizationId)
            .ToListAsync(cancellationToken);
        foreach (var role in customRoles.Where(role =>
                     RoleDefinitionCatalog.IsWithinTenantAdministratorPermissionCeiling(
                         role.Permissions.Select(permission => permission.Permission))))
        {
            roleKeys.Add(role.Key);
        }

        return roleKeys;
    }

    private static async Task<bool> CanManageAsync(HttpContext context, ICurrentUserAccessService accessService, string organizationId, CancellationToken cancellationToken)
    {
        var access = await accessService.ResolveAsync(context.User, cancellationToken);
        return access.IsHelpdeskAdmin || access.HasPermission(HelpdeskPermissions.TenantRolesAssign, organizationId);
    }

    private static async Task<bool> CanViewMembersAsync(HttpContext context, ICurrentUserAccessService accessService, string organizationId, CancellationToken cancellationToken)
    {
        var access = await accessService.ResolveAsync(context.User, cancellationToken);
        return access.IsHelpdeskAdmin ||
               access.HasPermission(HelpdeskPermissions.TenantUsersManage, organizationId) ||
               access.HasPermission(HelpdeskPermissions.TenantRolesAssign, organizationId);
    }

    private static string? ResolveOrganizationLookupPermission(string? permission) =>
        string.IsNullOrWhiteSpace(permission)
            ? HelpdeskPermissions.TenantRolesAssign
            : permission.Trim() switch
        {
            HelpdeskPermissions.TenantSettingsManage => HelpdeskPermissions.TenantSettingsManage,
            HelpdeskPermissions.TenantUsersManage => HelpdeskPermissions.TenantUsersManage,
            HelpdeskPermissions.TenantRolesAssign => HelpdeskPermissions.TenantRolesAssign,
            HelpdeskPermissions.TenantCustomersManage => HelpdeskPermissions.TenantCustomersManage,
            HelpdeskPermissions.TenantSlaManage => HelpdeskPermissions.TenantSlaManage,
            _ => null
        };

    private static async Task<bool> CanInviteAsync(HttpContext context, ICurrentUserAccessService accessService, string organizationId, CancellationToken cancellationToken)
    {
        var access = await accessService.ResolveAsync(context.User, cancellationToken);
        return access.IsHelpdeskAdmin ||
               (access.HasPermission(HelpdeskPermissions.TenantUsersManage, organizationId) &&
                access.HasPermission(HelpdeskPermissions.TenantRolesAssign, organizationId));
    }

    private static async Task<ApplicationUser?> FindTenantLocalUserAsync(
        string userId,
        string organizationId,
        HelpdeskDbContext db,
        UserManager<ApplicationUser> users,
        CancellationToken cancellationToken)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(user => !user.IsDeleted && user.Id == userId && (user.OrganizationId == organizationId ||
                db.ScopedRoleAssignments.Any(assignment => assignment.UserId == user.Id && assignment.OrganizationId == organizationId)), cancellationToken))
        {
            return null;
        }

        var account = await users.FindByIdAsync(userId);
        return account?.IsDeleted == true ? null : account;
    }

    private static string ResolveActorId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ??
        user.FindFirstValue("sub") ??
        user.Identity?.Name ??
        "unknown";

    public sealed record TenantSettingsResponse(string OrganizationId, string Name, string? ContactInfo);
    public sealed record UpdateTenantSettingsRequest(string Name, string? ContactInfo);
    public sealed record ReplaceTenantMembershipRequest(IReadOnlyList<string> RoleKeys);
    public sealed record TenantMembershipResponse(string UserId, string OrganizationId, IReadOnlyList<string> RoleKeys);
    public sealed record TenantOrganizationResponse(string Id, string Name);
    public sealed record TenantMemberResponse(string UserId, string Name, string Email, IReadOnlyList<string> RoleKeys);
    public sealed record TenantDelegableRoleResponse(string Key, string Name);
    public sealed record CreateTenantLocalAccountRequest(string DisplayName, string Email);
    public sealed record TenantLocalAccountInvitationResponse(string UserId, string Email, string ActivationToken);

    private sealed record TenantMemberCandidate(string Id, string Name, string Email);
}
