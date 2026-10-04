using System.Security.Claims;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Models;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.Auth.Rbac;

public sealed class CurrentUserAccessService : ICurrentUserAccessService
{
    private readonly HelpdeskDbContext _db;
    private readonly RatelDeskIdentityDbContext? _identityDb;

    public CurrentUserAccessService(HelpdeskDbContext db)
        : this(db, null)
    {
    }

    public CurrentUserAccessService(HelpdeskDbContext db, RatelDeskIdentityDbContext? identityDb)
    {
        _db = db;
        _identityDb = identityDb;
    }

    public async Task<CurrentUserAccessProfile> ResolveAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return Empty(false);
        }

        // Service subjects are never application accounts, even if an external-subject link was mistakenly created.
        if (user.HasClaim("auth_mode", "service") || user.HasClaim("auth_mode", "machine") || user.HasClaim("token_use", "rateldesk_service"))
            return Empty(true);

        var claimedLocalAccountId = IsLocalAccount(user)
            ? user.FindFirstValue(ClaimTypes.NameIdentifier)
            : null;
        var email = FirstClaim(user, ClaimTypes.Email, "email", "preferred_username");
        var issuer = FirstClaim(user, "iss")?.TrimEnd('/');
        var subject = FirstClaim(user, "sub");
        var authentikUserId = FirstClaim(user, "authentik_user_id", "ak_user_id");
        var link = await FindCustomerAuthLinkAsync(claimedLocalAccountId, issuer, subject, authentikUserId, ct);
        // A linked OIDC subject is not an application user ID. Resolve its
        // persisted application account before evaluating account state or
        // scoped role assignments.
        var localAccountId = claimedLocalAccountId ?? link?.LocalAccountId;
        ApplicationUser? localAccount = null;
        if (_identityDb is not null && !string.IsNullOrWhiteSpace(localAccountId))
        {
            localAccount = await _identityDb.Users.AsNoTracking()
                .SingleOrDefaultAsync(account => account.Id == localAccountId, ct);
            if (localAccount?.IsEnabled != true)
            {
                return Empty(true);
            }
        }

        // Local role claims are an authorization projection, never a source of
        // grants. OIDC groups remain an explicit, independent provider source.
        var groups = string.IsNullOrWhiteSpace(localAccountId)
            ? ClaimValues(user, user.HasClaim("permission_scope_mode", "scoped")
                    ? ["groups", "provider_role"]
                    : ["groups", "provider_role", ClaimTypes.Role, "roles"])
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scopedPermissionGrants = new HashSet<ScopedPermissionGrant>();
        var isAdmin = localAccount?.IsInstanceAdministrator == true ||
                      groups.Contains(HelpdeskPermissions.HelpdeskAdmin) ||
                      groups.Contains(AuthentikRbacGroups.HelpdeskAdmin);

        if (isAdmin)
        {
            permissions.Add(HelpdeskPermissions.HelpdeskAdmin);
            bundles.Add(HelpdeskRoleBundles.HelpdeskAdmin);
        }

        AddDirectPermissionClaims(groups, permissions);
        if (groups.Contains(AuthentikRbacGroups.ClientAdmin) || groups.Contains(HelpdeskPermissions.DataManagementAdmin))
        {
            bundles.Add(HelpdeskRoleBundles.DataManagementAdmin);
            permissions.Add(HelpdeskPermissions.DataManagementAdmin);
        }

        if (groups.Contains(AuthentikRbacGroups.Technical))
        {
            bundles.Add(HelpdeskRoleBundles.Technical);
            foreach (var permission in HelpdeskPermissions.TechnicalBundle)
            {
                permissions.Add(permission);
            }
        }

        Customer? customer = null;
        Organization? organization = null;
        if (link is not null)
        {
            customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == link.CustomerId, ct);
            if (customer?.IsEnabled == true)
            {
                organization = await _db.Organizations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == customer.OrganizationId, ct);
            }
        }

        var hasActiveCustomer = customer?.IsEnabled == true && organization?.IsEnabled == true;
        var domainUserId = link?.DomainUserId ?? localAccountId;
        var domainUser = string.IsNullOrWhiteSpace(domainUserId)
            ? null
            : await _db.Users.AsNoTracking().FirstOrDefaultAsync(domainUser => domainUser.Id == domainUserId, ct);
        var domainUserOrganization = domainUser is null || string.IsNullOrWhiteSpace(domainUser.OrganizationId)
            ? null
            : await _db.Organizations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == domainUser.OrganizationId, ct);
        var hasActiveDomainUser = domainUser is not null;
        // A missing DomainUserId marks a legacy external customer link. Once
        // linked to an application-managed user, only persisted assignments
        // supply its application roles, including the zero-assignment case.
        if (hasActiveCustomer && string.IsNullOrWhiteSpace(localAccountId) && string.IsNullOrWhiteSpace(link!.DomainUserId))
        {
            bundles.Add(HelpdeskRoleBundles.User);
            foreach (var permission in HelpdeskPermissions.UserBundle)
            {
                permissions.Add(permission);
            }

            if (groups.Contains(AuthentikRbacGroups.Technical))
            {
                bundles.Add(HelpdeskRoleBundles.Technical);
                foreach (var permission in HelpdeskPermissions.TechnicalBundle)
                {
                    permissions.Add(permission);
                }
            }
        }
        var providerPermissions = permissions.ToArray();
        if (hasActiveDomainUser)
        {
            var assignments = await (
                    from assignment in _db.ScopedRoleAssignments.AsNoTracking()
                    join assignmentOrganization in _db.Organizations.AsNoTracking()
                        on assignment.OrganizationId equals assignmentOrganization.Id
                    where assignment.UserId == domainUser!.Id && assignmentOrganization.IsEnabled
                    select assignment)
                .ToListAsync(ct);

            var assignedRoleKeys = assignments
                .Select(assignment => assignment.RoleKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var rolePermissions = await _db.Roles.AsNoTracking()
                .Where(role => assignedRoleKeys.Contains(role.Key) && role.Scope != RoleScopeKind.Instance)
                .Select(role => new
                {
                    role.Key,
                    role.OwnerOrganizationId,
                    Permissions = role.Permissions.Select(permission => permission.Permission).ToArray()
                })
                .ToDictionaryAsync(role => role.Key, StringComparer.OrdinalIgnoreCase, ct);
            foreach (var assignment in assignments)
            {
                if (rolePermissions.TryGetValue(assignment.RoleKey, out var persistedRole) &&
                    persistedRole.OwnerOrganizationId is not null &&
                    !string.Equals(persistedRole.OwnerOrganizationId, assignment.OrganizationId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var assignedPermissions = persistedRole is not null
                    ? (IReadOnlyList<string>)persistedRole.Permissions
                    : ScopedRoleCatalog.PermissionsFor(assignment.RoleKey);
                foreach (var permission in assignedPermissions)
                {
                    permissions.Add(permission);
                    scopedPermissionGrants.Add(new ScopedPermissionGrant(permission, assignment.OrganizationId));
                }
            }

        }

        var primaryOrganizationId = hasActiveCustomer
            ? customer!.OrganizationId
            : domainUserOrganization?.IsEnabled == true
                ? domainUser!.OrganizationId
                : null;
        var allowedOrganizations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var managedOrganizations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(primaryOrganizationId))
        {
            allowedOrganizations.Add(primaryOrganizationId);
        }
        foreach (var grant in scopedPermissionGrants)
        {
            allowedOrganizations.Add(grant.OrganizationId);
        }

        var isTechnical = bundles.Contains(HelpdeskRoleBundles.Technical);
        if (isTechnical && !string.IsNullOrWhiteSpace(primaryOrganizationId))
        {
            var managed = await _db.Organizations.AsNoTracking()
                .Where(x => x.State == Helpdesk.Shared.Models.EntityState.Enabled && x.ItSupportOrganizationId == primaryOrganizationId)
                .Select(x => x.Id)
                .ToListAsync(ct);

            foreach (var organizationId in managed)
            {
                allowedOrganizations.Add(organizationId);
                managedOrganizations.Add(organizationId);
            }
        }

        if (!string.IsNullOrWhiteSpace(localAccountId))
        {
            // Membership alone is not permission. A local account with no
            // assignments must remain without access after all later logins.
            allowedOrganizations.IntersectWith(scopedPermissionGrants.Select(grant => grant.OrganizationId));
        }
        else
        {
            var providerOrganizations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(primaryOrganizationId)) providerOrganizations.Add(primaryOrganizationId);
            providerOrganizations.UnionWith(managedOrganizations);
            foreach (var organizationId in providerOrganizations)
            foreach (var permission in providerPermissions)
                scopedPermissionGrants.Add(new ScopedPermissionGrant(permission, organizationId));
        }

        var profile = new CurrentUserAccessProfile(
            IsAuthenticated: true,
            Name: user.Identity?.Name ?? FirstClaim(user, "name", "preferred_username") ?? email,
            Email: email,
            PrimaryOrganizationId: primaryOrganizationId,
            PrimaryOrganizationName: organization?.Name ?? domainUserOrganization?.Name,
            CustomerId: hasActiveCustomer ? customer!.Id : null,
            IsHelpdeskAdmin: isAdmin,
            RoleBundles: bundles,
            Permissions: permissions,
            AllowedOrganizationIds: allowedOrganizations,
            ManagedOrganizationIds: managedOrganizations)
        {
            UsesScopedPermissions = true,
            ScopedPermissionGrants = scopedPermissionGrants
        };

        return await ConstrainIntegrationCredentialAsync(profile, user, ct);
    }

    private async Task<CustomerAuthLink?> FindCustomerAuthLinkAsync(
        string? localAccountId,
        string? issuer,
        string? subject,
        string? authentikUserId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(localAccountId))
        {
            var link = await _db.CustomerAuthLinks.AsNoTracking()
                .FirstOrDefaultAsync(x => x.LocalAccountId == localAccountId, ct);
            if (link is not null) return link;
        }

        if (!string.IsNullOrWhiteSpace(issuer) && !string.IsNullOrWhiteSpace(subject))
        {
            var link = await _db.CustomerAuthLinks.AsNoTracking()
                .FirstOrDefaultAsync(x => x.OidcIssuer == issuer && x.OidcSubject == subject, ct);
            if (link is not null) return link;
        }

        if (!string.IsNullOrWhiteSpace(authentikUserId))
        {
            var link = await _db.CustomerAuthLinks.AsNoTracking()
                .FirstOrDefaultAsync(x => x.AuthentikUserId == authentikUserId, ct);
            if (link is not null) return link;
        }

        return null;
    }

    private static bool IsLocalAccount(ClaimsPrincipal user) =>
        user.FindFirstValue("auth_mode") is "local" or "integration";

    private async Task<CurrentUserAccessProfile> ConstrainIntegrationCredentialAsync(
        CurrentUserAccessProfile profile,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (!string.Equals(user.FindFirstValue("auth_mode"), "integration", StringComparison.OrdinalIgnoreCase))
            return profile;

        var requestedPermissions = user.FindAll("integration_permission")
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedOrganizationId = user.FindFirstValue("integration_organization_id");
        if (requestedPermissions.Count == 0 || string.IsNullOrWhiteSpace(requestedOrganizationId) ||
            requestedPermissions.Any(permission => !HelpdeskPermissions.AssignablePermissions.Contains(permission, StringComparer.OrdinalIgnoreCase)))
            return Empty(true);

        var requestedOrganization = await _db.Organizations.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == requestedOrganizationId, ct);
        if (requestedOrganization?.State != Helpdesk.Shared.Models.EntityState.Enabled)
            return Empty(true);

        HashSet<ScopedPermissionGrant> effectiveGrants;
        if (profile.IsHelpdeskAdmin)
        {
            // Instance administration authorizes issuing a deliberately
            // bounded credential, never an instance-wide administrator token.
            effectiveGrants = requestedPermissions
                .Select(permission => new ScopedPermissionGrant(permission, requestedOrganization.Id))
                .ToHashSet();
        }
        else
        {
            effectiveGrants = profile.ScopedPermissionGrants
                .Where(grant => requestedPermissions.Contains(grant.Permission) &&
                                string.Equals(grant.OrganizationId, requestedOrganization.Id, StringComparison.OrdinalIgnoreCase))
                .Select(grant => new ScopedPermissionGrant(grant.Permission, requestedOrganization.Id))
                .ToHashSet();
        }

        if (effectiveGrants.Count == 0)
            return Empty(true);

        var effectivePermissions = effectiveGrants
            .Select(grant => grant.Permission)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var effectiveOrganizations = effectiveGrants
            .Select(grant => grant.OrganizationId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return profile with
        {
            PrimaryOrganizationId = requestedOrganization.Id,
            PrimaryOrganizationName = requestedOrganization.Name,
            CustomerId = string.Equals(profile.PrimaryOrganizationId, requestedOrganization.Id, StringComparison.OrdinalIgnoreCase)
                ? profile.CustomerId
                : null,
            IsHelpdeskAdmin = false,
            RoleBundles = effectivePermissions,
            Permissions = effectivePermissions,
            AllowedOrganizationIds = effectiveOrganizations,
            ManagedOrganizationIds = profile.ManagedOrganizationIds
                .Where(effectiveOrganizations.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            UsesScopedPermissions = true,
            ScopedPermissionGrants = effectiveGrants
        };
    }

    private static void AddDirectPermissionClaims(HashSet<string> groups, HashSet<string> permissions)
    {
        foreach (var permission in HelpdeskPermissions.AssignablePermissions)
        {
            if (groups.Contains(permission))
            {
                permissions.Add(permission);
            }
        }
    }

    private static IEnumerable<string> ClaimValues(ClaimsPrincipal user, params string[] claimTypes) =>
        claimTypes.SelectMany(type => user.FindAll(type))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value));

    private static string? FirstClaim(ClaimsPrincipal user, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = user.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private static CurrentUserAccessProfile Empty(bool authenticated) => new(
        authenticated,
        null,
        null,
        null,
        null,
        null,
        false,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
