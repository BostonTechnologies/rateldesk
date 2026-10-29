using System.Security.Claims;
using System.Text.Json;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Services;
using Helpdesk.Shared.DTOs.McpPolicies;
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
    // Empty retains the original one-organization permission policy unchanged.
    public string DefinitionJson { get; set; } = "";
}
public sealed record McpPolicyDefinitionDocument(int Version, string Resource, McpPolicyScopeDraft[] Scopes);

public static class McpPolicyDefinitionDocumentReader
{
    public static McpPolicyDefinitionDocument? Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var document = JsonSerializer.Deserialize<McpPolicyDefinitionDocument>(json);
            return document is { Version: 1, Scopes.Length: 1 } &&
                   document.Scopes[0] is { Permissions: not null, Operations: not null, Exceptions: not null } &&
                   !string.IsNullOrWhiteSpace(document.Resource)
                ? document : null;
        }
        catch (JsonException) { return null; }
    }
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
        if (!string.IsNullOrEmpty(policy.DefinitionJson))
        {
            var definition = McpPolicyDefinitionDocumentReader.Read(policy.DefinitionJson);
            var scope = definition?.Scopes.Single();
            var active = policy.State == "Active" && policy.ExpiresUtc > DateTime.UtcNow &&
                         scope is { Effect: "Allow" } && scope.ExpiresUtc > DateTime.UtcNow &&
                         string.Equals(definition!.Resource, principal.FindFirstValue("mcp_resource_uri"), StringComparison.Ordinal);
            if (active && scope!.Scope == "server" && profile.IsHelpdeskAdmin &&
                scope.Permissions.Contains(Helpdesk.Shared.Auth.HelpdeskPermissions.HelpdeskAdmin, StringComparer.Ordinal))
            {
                // The API execution authentication gate checks the current, durable
                // operation policy for every request before accepting this principal.
                return profile;
            }
            return Constrain(profile, scope?.OrganizationId ?? policy.OrganizationId,
                active && scope!.Scope == "organization" ? scope.Permissions : []);
        }
        var allowed = JsonSerializer.Deserialize<string[]>(policy.PermissionsJson) ?? [];
        return Constrain(profile, policy.OrganizationId,
            policy.State == "Active" && policy.ExpiresUtc > DateTime.UtcNow ? allowed : []);
    }

    private static CurrentUserAccessProfile Constrain(CurrentUserAccessProfile profile, string organizationId, string[] allowed)
    {
        var permissions = allowed.Where(permission => profile.HasPermission(permission, organizationId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return profile with
        {
            IsHelpdeskAdmin = false, PrimaryOrganizationId = permissions.Count > 0 ? organizationId : null,
            PrimaryOrganizationName = permissions.Count > 0 && string.Equals(profile.PrimaryOrganizationId, organizationId, StringComparison.OrdinalIgnoreCase) ? profile.PrimaryOrganizationName : null,
            CustomerId = permissions.Count > 0 && string.Equals(profile.PrimaryOrganizationId, organizationId, StringComparison.OrdinalIgnoreCase) ? profile.CustomerId : null,
            UsesScopedPermissions = true,
            Permissions = permissions, RoleBundles = permissions,
            AllowedOrganizationIds = permissions.Count > 0 ? new HashSet<string> { organizationId } : new HashSet<string>(),
            ManagedOrganizationIds = new HashSet<string>(),
            ScopedPermissionGrants = permissions.Select(x => new ScopedPermissionGrant(x, organizationId)).ToHashSet()
        };
    }
}

