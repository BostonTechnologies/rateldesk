using System.Security.Claims;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Services;
using Helpdesk.Infrastructure.ServiceIdentity;

namespace Helpdesk.API.Middleware;

public sealed class UserAccessClaimsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ICurrentUserAccessService accessService)
    {
        if (context.User.Identity?.IsAuthenticated == true && ServicePrincipalRegistry.IsMachinePrincipal(context.User))
        {
            // The service handler has verified these grant claims. Human account
            // projection must neither erase them nor add application roles.
            await next(context);
            return;
        }
        if (context.User.Identity?.IsAuthenticated == true && context.User.Identity is ClaimsIdentity identity)
        {
            // Preserve verified upstream roles before replacing the role claims
            // with the application projection. Subsequent endpoint resolution
            // must not mistake that projection for new provider authority.
            if (!identity.HasClaim("permission_scope_mode", "scoped"))
            {
                foreach (var claim in identity.Claims.Where(claim => claim.Type is ClaimTypes.Role or "roles").ToArray())
                    AddClaim(identity, "provider_role", claim.Value);
            }
            RemoveScopedAccessClaims(identity, preserveSourceMarker: true);
            var access = await accessService.ResolveAsync(context.User, context.RequestAborted);
            RemoveApplicationAccessClaims(identity);
            AddClaim(identity, "permission_scope_mode", "scoped");
            AddClaim(identity, "organization_id", access.PrimaryOrganizationId);
            AddClaim(identity, "customer_id", access.CustomerId);
            foreach (var organizationId in access.AllowedOrganizationIds)
            {
                AddClaim(identity, "allowed_organization_id", organizationId);
            }
            foreach (var grant in access.ScopedPermissionGrants)
            {
                AddClaim(identity, "scoped_permission", grant.ToString());
            }
            foreach (var role in access.RoleBundles.Concat(access.Permissions))
            {
                AddClaim(identity, ClaimTypes.Role, role);
                AddClaim(identity, "roles", role);
            }
        }

        await next(context);
    }

    private static void RemoveApplicationAccessClaims(ClaimsIdentity identity)
    {
        RemoveScopedAccessClaims(identity);

        foreach (var claim in identity.Claims.Where(claim =>
                     claim.Type is ClaimTypes.Role or "roles" &&
                     IsApplicationRoleValue(claim.Value)).ToArray())
        {
            identity.RemoveClaim(claim);
        }
    }

    private static void RemoveScopedAccessClaims(ClaimsIdentity identity, bool preserveSourceMarker = false)
    {
        foreach (var claim in identity.Claims.Where(claim => claim.Type is
                     "organization_id" or
                     "allowed_organization_id" or
                     "customer_id" or
                     "scoped_permission" or
                     "permission_scope_mode").Where(claim => !preserveSourceMarker || claim.Type != "permission_scope_mode").ToArray())
        {
            identity.RemoveClaim(claim);
        }
    }

    private static void AddClaim(ClaimsIdentity identity, string type, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (identity.HasClaim(type, value)) return;
        identity.AddClaim(new Claim(type, value));
    }

    private static bool IsApplicationRoleValue(string value) =>
        value is HelpdeskRoleBundles.User or
            HelpdeskRoleBundles.Technical or
            HelpdeskRoleBundles.DataManagementAdmin or
            HelpdeskRoleBundles.HelpdeskAdmin or
            HelpdeskPermissions.HelpdeskAdmin ||
        HelpdeskPermissions.AssignablePermissions.Contains(value, StringComparer.OrdinalIgnoreCase);
}
