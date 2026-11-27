using Helpdesk.Shared.Models;

namespace Helpdesk.Shared.Auth;

public sealed record BuiltInRoleDefinition(
    string Key,
    string Name,
    RoleScopeKind Scope,
    IReadOnlyList<string> Permissions,
    bool IsProtected = true);

public static class RoleDefinitionCatalog
{
    // Tenant administrators may compose operational roles for a tenant, but
    // cannot delegate tenant administration or instance-wide capabilities.
    public static readonly IReadOnlySet<string> TenantAdministratorPermissionCeiling =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            HelpdeskPermissions.SelfServiceUser,
            HelpdeskPermissions.IncidentUser,
            HelpdeskPermissions.IncidentRead,
            HelpdeskPermissions.IncidentWrite,
            HelpdeskPermissions.IncidentDelete,
            HelpdeskPermissions.RequestUser,
            HelpdeskPermissions.RequestRead,
            HelpdeskPermissions.RequestWrite,
            HelpdeskPermissions.RequestDelete,
            HelpdeskPermissions.RequestExecute,
            HelpdeskPermissions.ChangeUser,
            HelpdeskPermissions.ChangeRead,
            HelpdeskPermissions.ChangeWrite,
            HelpdeskPermissions.ChangeDelete,
            HelpdeskPermissions.ChangeApprove,
            HelpdeskPermissions.TicketQueueTransfer
        };

    public static bool IsQueueScopedPermission(string permission) => permission is
        HelpdeskPermissions.IncidentRead or HelpdeskPermissions.IncidentWrite or HelpdeskPermissions.IncidentDelete or
        HelpdeskPermissions.RequestRead or HelpdeskPermissions.RequestWrite or HelpdeskPermissions.RequestDelete or HelpdeskPermissions.RequestExecute or
        HelpdeskPermissions.ChangeRead or HelpdeskPermissions.ChangeWrite or HelpdeskPermissions.ChangeDelete or HelpdeskPermissions.ChangeApprove or
        HelpdeskPermissions.TicketQueueTransfer;

    public static bool IsWorkforceTicketPermission(string permission) => IsQueueScopedPermission(permission) || permission is
        HelpdeskPermissions.IncidentManager or HelpdeskPermissions.RequestManager or HelpdeskPermissions.ChangeManager;

    public static bool IsOwnTicketPermission(string permission) => permission is
        HelpdeskPermissions.IncidentUser or HelpdeskPermissions.RequestUser or HelpdeskPermissions.ChangeUser;

    public static readonly IReadOnlyList<BuiltInRoleDefinition> BuiltIns =
    [
        new(
            "InstanceAdministrator",
            "Instance Administrator",
            RoleScopeKind.Instance,
            [HelpdeskPermissions.HelpdeskAdmin]),
        new(
            ScopedRoleCatalog.SelfServiceUser,
            "Self-service User",
            RoleScopeKind.OwnResource,
            HelpdeskPermissions.UserBundle),
        new(
            ScopedRoleCatalog.TenantAdministrator,
            "Tenant Administrator",
            RoleScopeKind.Tenant,
            [
                HelpdeskPermissions.TenantUsersManage,
                HelpdeskPermissions.TenantRolesAssign,
                HelpdeskPermissions.TenantSettingsManage,
                HelpdeskPermissions.TenantCustomersManage,
                HelpdeskPermissions.TenantSlaManage
            ]),
        new(
            ScopedRoleCatalog.Technician,
            "Technician",
            RoleScopeKind.Tenant,
            HelpdeskPermissions.OperatorBundle),
        new(ScopedRoleCatalog.IncidentReader, "Incident Reader", RoleScopeKind.Tenant,
            [HelpdeskPermissions.IncidentRead]),
        new(ScopedRoleCatalog.IncidentWriter, "Incident Writer", RoleScopeKind.Tenant,
            [HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite]),
        new(ScopedRoleCatalog.RequestReader, "Request Reader", RoleScopeKind.Tenant,
            [HelpdeskPermissions.RequestRead]),
        new(ScopedRoleCatalog.RequestWriter, "Request Writer", RoleScopeKind.Tenant,
            [HelpdeskPermissions.RequestRead, HelpdeskPermissions.RequestWrite]),
        new(ScopedRoleCatalog.RequestExecutor, "Request Executor", RoleScopeKind.Tenant,
            [HelpdeskPermissions.RequestRead, HelpdeskPermissions.RequestExecute]),
        new(ScopedRoleCatalog.ChangeReader, "Change Reader", RoleScopeKind.Tenant,
            [HelpdeskPermissions.ChangeRead]),
        new(ScopedRoleCatalog.ChangeWriter, "Change Writer", RoleScopeKind.Tenant,
            [HelpdeskPermissions.ChangeRead, HelpdeskPermissions.ChangeWrite]),
        new(ScopedRoleCatalog.ChangeApprover, "Change Approver", RoleScopeKind.Tenant,
            [HelpdeskPermissions.ChangeRead, HelpdeskPermissions.ChangeApprove])
    ];

    public static BuiltInRoleDefinition? Find(string? key) =>
        BuiltIns.FirstOrDefault(definition =>
            string.Equals(definition.Key, key, StringComparison.OrdinalIgnoreCase));

    public static bool IsAssignablePermission(string permission) =>
        HelpdeskPermissions.AssignablePermissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

    public static bool IsWithinTenantAdministratorPermissionCeiling(IEnumerable<string> permissions) =>
        permissions.All(TenantAdministratorPermissionCeiling.Contains);

    public static IReadOnlyList<string> NormalizePermissions(IEnumerable<string>? permissions) =>
        (permissions ?? [])
        .Where(permission => !string.IsNullOrWhiteSpace(permission))
        .Select(permission => HelpdeskPermissions.AssignablePermissions.FirstOrDefault(known =>
            string.Equals(known, permission.Trim(), StringComparison.OrdinalIgnoreCase)) ?? permission.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(permission => permission, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static bool SatisfiesDependencies(IReadOnlyCollection<string> permissions)
    {
        var grants = permissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (!grants.Contains(HelpdeskPermissions.IncidentManager) || grants.Contains(HelpdeskPermissions.IncidentUser)) &&
               (!grants.Contains(HelpdeskPermissions.RequestManager) || grants.Contains(HelpdeskPermissions.RequestUser)) &&
               (!grants.Contains(HelpdeskPermissions.ChangeManager) || grants.Contains(HelpdeskPermissions.ChangeUser)) &&
               RequiresRead(grants, HelpdeskPermissions.IncidentRead, HelpdeskPermissions.IncidentWrite, HelpdeskPermissions.IncidentDelete) &&
               RequiresRead(grants, HelpdeskPermissions.RequestRead, HelpdeskPermissions.RequestWrite, HelpdeskPermissions.RequestDelete, HelpdeskPermissions.RequestExecute) &&
               RequiresRead(grants, HelpdeskPermissions.ChangeRead, HelpdeskPermissions.ChangeWrite, HelpdeskPermissions.ChangeDelete, HelpdeskPermissions.ChangeApprove);
    }

    private static bool RequiresRead(HashSet<string> grants, string read, params string[] actions)
    {
        return !actions.Any(grants.Contains) || grants.Contains(read);
    }
}
