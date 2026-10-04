using System.Security.Claims;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Authentication;

/// <summary>Resolves the application account allowed to manage integration credentials.</summary>
public interface IIntegrationCredentialOwnerResolver
{
    Task<IntegrationCredentialOwner?> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

public sealed record IntegrationCredentialOwner(string UserId);

public sealed class IntegrationCredentialOwnerResolver(
    HelpdeskDbContext db,
    RatelDeskIdentityDbContext identityDb) : IIntegrationCredentialOwnerResolver
{
    public async Task<IntegrationCredentialOwner?> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (principal.Identity?.IsAuthenticated != true ||
            principal.HasClaim("auth_mode", "integration") ||
            principal.HasClaim("auth_mode", "mcp") ||
            principal.HasClaim("auth_mode", "gateway") ||
            principal.HasClaim("auth_mode", "service") ||
            principal.HasClaim("auth_mode", "machine") ||
            principal.HasClaim("token_use", "rateldesk_service") ||
            principal.HasClaim("integration_purpose", "mcp"))
            return null;

        if (principal.HasClaim("auth_mode", "local"))
            return await ResolveEnabledAccountAsync(principal.FindFirstValue(ClaimTypes.NameIdentifier), cancellationToken);

        var issuer = principal.FindFirstValue("iss")?.TrimEnd('/');
        var subject = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
            return null;

        var localAccountId = await db.CustomerAuthLinks.AsNoTracking()
            .Where(link => link.OidcSubject == subject &&
                           (link.OidcIssuer == issuer || link.OidcIssuer == $"{issuer}/"))
            .Select(link => link.LocalAccountId)
            .SingleOrDefaultAsync(cancellationToken);

        return await ResolveEnabledAccountAsync(localAccountId, cancellationToken);
    }

    private async Task<IntegrationCredentialOwner?> ResolveEnabledAccountAsync(string? userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        var enabled = await identityDb.Users.AsNoTracking()
            .AnyAsync(user => user.Id == userId && user.IsEnabled, cancellationToken);
        return enabled ? new IntegrationCredentialOwner(userId) : null;
    }
}

public sealed class IntegrationCredentialManagementSessionRequirement : IAuthorizationRequirement;

public sealed class IntegrationCredentialManagementSessionHandler(
    IIntegrationCredentialOwnerResolver ownerResolver)
    : AuthorizationHandler<IntegrationCredentialManagementSessionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        IntegrationCredentialManagementSessionRequirement requirement)
    {
        if (await ownerResolver.ResolveAsync(context.User) is not null)
            context.Succeed(requirement);
    }
}
