using System.Security.Claims;
using Helpdesk.Shared.Auth;

namespace Helpdesk.Shared.Services;

public sealed record CurrentUserAccessProfile(
    bool IsAuthenticated,
    string? Name,
    string? Email,
    string? PrimaryOrganizationId,
    string? PrimaryOrganizationName,
    string? CustomerId,
    bool IsHelpdeskAdmin,
    IReadOnlySet<string> RoleBundles,
    IReadOnlySet<string> Permissions,
    IReadOnlySet<string> AllowedOrganizationIds,
    IReadOnlySet<string> ManagedOrganizationIds)
{
    public bool UsesScopedPermissions { get; init; }

    public IReadOnlySet<ScopedPermissionGrant> ScopedPermissionGrants { get; init; } =
        new HashSet<ScopedPermissionGrant>();

    public IReadOnlySet<TicketQueuePermissionGrant> TicketQueuePermissionGrants { get; init; } =
        new HashSet<TicketQueuePermissionGrant>();

    public IReadOnlySet<string> RestrictedTicketOrganizationIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public string? DomainUserId { get; init; }

    public bool HasProjectedWorkforceRestriction { get; init; }

    public bool HasRestrictedWorkforceAccess => !IsHelpdeskAdmin &&
        (RestrictedTicketOrganizationIds.Count > 0 || HasProjectedWorkforceRestriction);

    public bool HasPermission(string permission, string? organizationId, string? supportGroupId) =>
        HasPermission(permission, organizationId) || (supportGroupId != null && TicketQueuePermissionGrants.Any(grant =>
            grant.Permission == permission && grant.OrganizationId == organizationId && grant.SupportGroupId == supportGroupId));

    public bool HasPermissionInAnyQueue(string permission, string? organizationId) =>
        HasPermission(permission, organizationId) || TicketQueuePermissionGrants.Any(grant =>
            string.Equals(grant.Permission, permission, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(grant.OrganizationId, organizationId, StringComparison.OrdinalIgnoreCase));

    public bool HasPermission(string permission) =>
        IsHelpdeskAdmin || Permissions.Contains(permission);

    public bool HasPermission(string permission, string? organizationId)
    {
        if (IsHelpdeskAdmin)
        {
            return true;
        }

        if (UsesScopedPermissions || ScopedPermissionGrants.Count > 0)
        {
            return !string.IsNullOrWhiteSpace(organizationId) &&
                   ScopedPermissionGrants.Any(grant =>
                       string.Equals(grant.Permission, permission, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(grant.OrganizationId, organizationId, StringComparison.OrdinalIgnoreCase));
        }

        return InAllowedOrg(organizationId) && Permissions.Contains(permission);
    }

    public IReadOnlySet<string> OrganizationIdsFor(string permission) =>
        UsesScopedPermissions || ScopedPermissionGrants.Count > 0
            ? ScopedPermissionGrants
                .Where(grant => string.Equals(grant.Permission, permission, StringComparison.OrdinalIgnoreCase))
                .Select(grant => grant.OrganizationId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : HasPermission(permission)
                ? AllowedOrganizationIds
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> OrganizationIdsForAny(params string[] permissions) =>
        permissions.SelectMany(OrganizationIdsFor).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool CanManageIntegrationCredentials =>
        IsAuthenticated && (IsHelpdeskAdmin || OrganizationIdsFor(HelpdeskPermissions.TenantSettingsManage).Count > 0);

    public bool CanManageIntegrationCredentialsFor(string? organizationId) =>
        IsAuthenticated && HasPermission(HelpdeskPermissions.TenantSettingsManage, organizationId);

    public bool CanViewIncident(string? organizationId, string? customerId, string? requesterEmail) =>
        HasPermission(HelpdeskPermissions.IncidentRead, organizationId) || CanManageIncident(organizationId) ||
        CanOwn(HelpdeskPermissions.IncidentUser, organizationId, customerId, requesterEmail);

    public bool CanManageIncident(string? organizationId) =>
        HasPermission(HelpdeskPermissions.IncidentWrite, organizationId) ||
        HasPermission(HelpdeskPermissions.IncidentManager, organizationId);

    public bool CanDeleteIncident(string? organizationId) =>
        HasPermission(HelpdeskPermissions.IncidentDelete, organizationId) ||
        HasPermission(HelpdeskPermissions.IncidentManager, organizationId);

    public bool CanContributeIncident(string? organizationId, string? customerId, string? requesterEmail) =>
        CanManageIncident(organizationId) || CanOwn(HelpdeskPermissions.IncidentUser, organizationId, customerId, requesterEmail);

    public bool CanCreateIncident(string? organizationId, string? customerId, string? requesterEmail) =>
        CanManageIncident(organizationId) || CanOwn(HelpdeskPermissions.IncidentUser, organizationId, customerId, requesterEmail);

    public bool CanViewRequest(string? organizationId, string? customerId, string? requesterEmail) =>
        HasPermission(HelpdeskPermissions.RequestRead, organizationId) || CanManageRequest(organizationId) ||
        CanOwn(HelpdeskPermissions.RequestUser, organizationId, customerId, requesterEmail);

    public bool CanManageRequest(string? organizationId) =>
        HasPermission(HelpdeskPermissions.RequestWrite, organizationId) ||
        HasPermission(HelpdeskPermissions.RequestManager, organizationId);

    public bool CanDeleteRequest(string? organizationId) =>
        HasPermission(HelpdeskPermissions.RequestDelete, organizationId) ||
        HasPermission(HelpdeskPermissions.RequestManager, organizationId);

    public bool CanContributeRequest(string? organizationId, string? customerId, string? requesterEmail) =>
        CanManageRequest(organizationId) || CanOwn(HelpdeskPermissions.RequestUser, organizationId, customerId, requesterEmail);

    public bool CanCreateRequest(string? organizationId, string? customerId, string? requesterEmail) =>
        CanManageRequest(organizationId) || CanOwn(HelpdeskPermissions.RequestUser, organizationId, customerId, requesterEmail);

    public bool CanExecuteRequest(string? organizationId) =>
        HasPermission(HelpdeskPermissions.RequestExecute, organizationId) ||
        HasPermission(HelpdeskPermissions.RequestManager, organizationId);

    public bool CanViewChange(string? organizationId, string? customerId, string? requesterEmail) =>
        HasPermission(HelpdeskPermissions.ChangeRead, organizationId) || CanManageChange(organizationId) ||
        CanOwn(HelpdeskPermissions.ChangeUser, organizationId, customerId, requesterEmail);

    public bool CanManageChange(string? organizationId) =>
        HasPermission(HelpdeskPermissions.ChangeWrite, organizationId) ||
        HasPermission(HelpdeskPermissions.ChangeManager, organizationId);

    public bool CanDeleteChange(string? organizationId) =>
        HasPermission(HelpdeskPermissions.ChangeDelete, organizationId) ||
        HasPermission(HelpdeskPermissions.ChangeManager, organizationId);

    public bool CanContributeChange(string? organizationId, string? customerId, string? requesterEmail) =>
        CanManageChange(organizationId) || CanOwn(HelpdeskPermissions.ChangeUser, organizationId, customerId, requesterEmail);

    public bool CanCreateChange(string? organizationId, string? customerId, string? requesterEmail) =>
        CanManageChange(organizationId) || CanOwn(HelpdeskPermissions.ChangeUser, organizationId, customerId, requesterEmail);

    public bool CanApproveChange(string? organizationId) =>
        HasPermission(HelpdeskPermissions.ChangeApprove, organizationId) ||
        HasPermission(HelpdeskPermissions.ChangeManager, organizationId);

    public static CurrentUserAccessProfile FromClaims(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return Empty(false);
        }

        var roleValues = user.FindAll(ClaimTypes.Role)
            .Concat(user.FindAll("roles"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isAdmin =
            user.IsInRole(HelpdeskPermissions.HelpdeskAdmin) ||
            roleValues.Contains(HelpdeskPermissions.HelpdeskAdmin) ||
            roleValues.Contains(AuthentikRbacGroups.HelpdeskAdmin);

        var scopedPermissionGrants = user.FindAll("scoped_permission")
            .Select(claim => ScopedPermissionGrant.TryParse(claim.Value))
            .OfType<ScopedPermissionGrant>()
            .ToHashSet();

        var allowedOrganizationIds = user.FindAll("allowed_organization_id")
            .Select(claim => claim.Value)
            .Append(FirstClaim(user, "organization_id"))
            .OfType<string>()
            .Concat(scopedPermissionGrants.Select(grant => grant.OrganizationId))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new CurrentUserAccessProfile(
            true,
            user.Identity?.Name ?? FirstClaim(user, "name", "preferred_username", ClaimTypes.Email, "email"),
            FirstClaim(user, ClaimTypes.Email, "email", "preferred_username"),
            FirstClaim(user, "organization_id"),
            null,
            FirstClaim(user, "customer_id"),
            isAdmin,
            roleValues,
            roleValues,
            allowedOrganizationIds,
            user.FindAll("managed_organization_id")
                .Select(claim => claim.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase))
        {
            UsesScopedPermissions = user.HasClaim("permission_scope_mode", "scoped"),
            ScopedPermissionGrants = scopedPermissionGrants,
            HasProjectedWorkforceRestriction = user.HasClaim("ticket_workforce_scope", "restricted"),
            TicketQueuePermissionGrants = user.FindAll("ticket_queue_permission")
                .Select(claim => TicketQueuePermissionGrant.TryParse(claim.Value)).OfType<TicketQueuePermissionGrant>().ToHashSet()
        };
    }

    private static string? FirstClaim(ClaimsPrincipal user, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = user.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
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

    private bool CanOwn(string permission, string? organizationId, string? customerId, string? requesterEmail) =>
        HasPermission(permission, organizationId) &&
        (organizationId == null || !RestrictedTicketOrganizationIds.Contains(organizationId)) &&
        !string.IsNullOrWhiteSpace(customerId) &&
        string.Equals(customerId, CustomerId, StringComparison.OrdinalIgnoreCase);

    private bool InAllowedOrg(string? organizationId) =>
        !string.IsNullOrWhiteSpace(organizationId) && AllowedOrganizationIds.Contains(organizationId);
}

public sealed record TicketQueuePermissionGrant(string Permission, string OrganizationId, string SupportGroupId, string OwnerOrganizationId)
{
    public override string ToString() => $"{Permission}|{OrganizationId}|{SupportGroupId}|{OwnerOrganizationId}";

    public static TicketQueuePermissionGrant? TryParse(string value)
    {
        var parts = value.Split('|');
        return parts.Length == 4 && parts.All(part => !string.IsNullOrWhiteSpace(part))
            ? new TicketQueuePermissionGrant(parts[0], parts[1], parts[2], parts[3]) : null;
    }
}

public sealed record ScopedPermissionGrant(string Permission, string OrganizationId)
{
    public override string ToString() => $"{Permission}|{OrganizationId}";

    public static ScopedPermissionGrant? TryParse(string value)
    {
        var separator = value.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            return null;
        }

        var permission = value[..separator].Trim();
        var organizationId = value[(separator + 1)..].Trim();
        return string.IsNullOrWhiteSpace(permission) || string.IsNullOrWhiteSpace(organizationId)
            ? null
            : new ScopedPermissionGrant(permission, organizationId);
    }
}
