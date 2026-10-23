using System.Security.Claims;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Shared.Auth;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Authentication;

/// <summary>Attributes business administration to a human or an explicitly delegated owner.</summary>
public static class AdministrationOwnerResolver
{
    public static async Task<IntegrationCredentialOwner?> ResolveAsync(ClaimsPrincipal principal,
        IIntegrationCredentialOwnerResolver interactiveOwners, RatelDeskIdentityDbContext identityDb,
        CancellationToken cancellationToken = default)
    {
        var interactive = await interactiveOwners.ResolveAsync(principal, cancellationToken);
        if (interactive is not null) return interactive;
        if (principal.Identity is not { IsAuthenticated: true, AuthenticationType: McpExecutionAuthenticationHandler.SchemeName } ||
            !principal.HasClaim("auth_mode", "integration") || !principal.HasClaim("token_use", "mcp_execution") ||
            !principal.HasClaim("integration_purpose", IntegrationCredentialGrantKinds.OwnerAdministrationPurpose) ||
            !Guid.TryParseExact(principal.FindFirstValue("integration_credential_id"), "N", out var credentialId)) return null;

        var credential = await identityDb.IntegrationCredentials.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == credentialId, cancellationToken);
        if (credential is null || credential.Purpose != IntegrationCredentialGrantKinds.OwnerAdministrationPurpose ||
            credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            credential.Permissions != HelpdeskPermissions.HelpdeskAdmin ||
            credential.OwnerUserId != principal.FindFirstValue(ClaimTypes.NameIdentifier) ||
            credential.McpResourceUri != principal.FindFirstValue("mcp_resource_uri")) return null;
        return await identityDb.Users.AsNoTracking().AnyAsync(account => account.Id == credential.OwnerUserId &&
            account.IsEnabled && account.IsInstanceAdministrator, cancellationToken)
            ? new IntegrationCredentialOwner(credential.OwnerUserId) : null;
    }
}
