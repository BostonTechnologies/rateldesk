using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Infrastructure.McpPolicies;

public sealed class McpAccessPolicy
{
    public Guid CredentialId { get; set; }
    public string OrganizationId { get; set; } = "";
    public string PermissionsJson { get; set; } = "[]";
    public DateTime ExpiresUtc { get; set; }
    public int Revision { get; set; }
    public string State { get; set; } = "Active";
}
public sealed class McpAccessPolicyAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CredentialId { get; set; }
    public int Revision { get; set; }
    public string Actor { get; set; } = "";
    public DateTime OccurredUtc { get; set; } = DateTime.UtcNow;
    public string DefinitionJson { get; set; } = "";
}
public static class McpAccessPolicyEnforcement
{
    public static async Task<CurrentUserAccessProfile> ApplyAsync(HelpdeskDbContext db, CurrentUserAccessProfile profile, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!principal.HasClaim("auth_mode", "integration") || !Guid.TryParse(principal.FindFirstValue("integration_credential_id"), out var id)) return profile;
        var policy = await db.Set<McpAccessPolicy>().AsNoTracking().SingleOrDefaultAsync(x => x.CredentialId == id, ct);
        if (policy is null) return profile;
        var allowed = JsonSerializer.Deserialize<string[]>(policy.PermissionsJson) ?? [];
        var permissions = policy.State == "Active" && policy.ExpiresUtc > DateTime.UtcNow
            ? allowed.Where(permission => profile.HasPermission(permission, policy.OrganizationId)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return profile with
        {
            IsHelpdeskAdmin = false, PrimaryOrganizationId = permissions.Count > 0 ? policy.OrganizationId : null,
            PrimaryOrganizationName = permissions.Count > 0 && string.Equals(profile.PrimaryOrganizationId, policy.OrganizationId, StringComparison.OrdinalIgnoreCase) ? profile.PrimaryOrganizationName : null,
            CustomerId = permissions.Count > 0 && string.Equals(profile.PrimaryOrganizationId, policy.OrganizationId, StringComparison.OrdinalIgnoreCase) ? profile.CustomerId : null,
            UsesScopedPermissions = true,
            Permissions = permissions, RoleBundles = permissions,
            AllowedOrganizationIds = permissions.Count > 0 ? new HashSet<string> { policy.OrganizationId } : new HashSet<string>(),
            ManagedOrganizationIds = new HashSet<string>(),
            ScopedPermissionGrants = permissions.Select(x => new ScopedPermissionGrant(x, policy.OrganizationId)).ToHashSet()
        };
    }
}
