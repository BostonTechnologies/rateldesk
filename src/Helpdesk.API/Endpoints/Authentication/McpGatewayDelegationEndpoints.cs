using System.Security.Claims;
using Helpdesk.API.Authentication;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Endpoints.Authentication;

/// <summary>
/// The only API endpoint that accepts an MCP-purpose opaque credential. It
/// exchanges that gateway credential for a short-lived execution credential
/// after resolving the owner's current authorization at the API authority.
/// </summary>
public static class McpGatewayDelegationEndpoints
{
    public const string DelegationPolicy = "McpCredentialDelegation";
    public const string ResourceHeader = "X-RatelDesk-Mcp-Resource";
    private static readonly TimeSpan ExecutionLifetime = TimeSpan.FromMinutes(2);

    public static void MapMcpGatewayDelegationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/mcp/execution-token", async (
            HttpContext context,
            ClaimsPrincipal principal,
            RatelDeskIdentityDbContext identityDb,
            ICurrentUserAccessService accessService,
            McpExecutionTokenService executionTokens,
            CancellationToken ct) =>
        {
            var resourceUri = CanonicalResourceUri(context.Request.Headers[ResourceHeader].ToString());
            var credentialId = principal.FindFirstValue("integration_credential_id");
            if (resourceUri is null || !Guid.TryParseExact(credentialId, "N", out var parsedCredentialId))
                return Results.Unauthorized();

            var credential = await identityDb.IntegrationCredentials
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == parsedCredentialId, ct)
                .ConfigureAwait(false);
            if (credential is null || !string.Equals(credential.McpResourceUri, resourceUri, StringComparison.Ordinal))
                return Results.Forbid();

            var owner = await identityDb.Users.AsNoTracking()
                .SingleOrDefaultAsync(user => user.Id == credential.OwnerUserId, ct)
                .ConfigureAwait(false);
            if (owner?.IsEnabled != true)
                return Results.Unauthorized();

            var access = await accessService.ResolveAsync(principal, ct).ConfigureAwait(false);
            if (!access.IsAuthenticated || (access.Permissions.Count == 0 && credential.Purpose != IntegrationCredentialIssuer.SetupVerificationPurpose) || access.AllowedOrganizationIds.Count == 0)
                return Results.Forbid();

            if (credential.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose &&
                (access.Permissions.Count != 0 || access.AllowedOrganizationIds.Count != 1 ||
                 !access.AllowedOrganizationIds.Contains(credential.OrganizationId!))) return Results.Forbid();

            var expiresAtUtc = DateTimeOffset.UtcNow.Add(ExecutionLifetime);
            if (expiresAtUtc > credential.ExpiresAtUtc) expiresAtUtc = credential.ExpiresAtUtc;
            var token = executionTokens.Create(credential, owner, resourceUri, expiresAtUtc);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new McpExecutionTokenResponse(
                token,
                expiresAtUtc,
                owner.Id,
                owner.DisplayName ?? owner.UserName ?? owner.Email ?? owner.Id,
                access.PrimaryOrganizationId,
                access.Permissions.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                access.AllowedOrganizationIds.Order(StringComparer.OrdinalIgnoreCase).ToArray())
                { SetupVerification = credential.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose });
        })
        .RequireAuthorization(DelegationPolicy)
        .WithTags("MCP Gateway")
        .WithSummary("Exchanges a paired MCP credential for a short-lived execution credential.")
        .WithDescription("Only paired MCP credentials can call this endpoint. The resulting execution credential is API-only and is never valid at the MCP gateway.");
    }

    public static string? CanonicalResourceUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(uri.AbsolutePath.TrimEnd('/'), "/mcp", StringComparison.Ordinal))
        {
            return null;
        }

        return uri.AbsoluteUri.TrimEnd('/');
    }

    public sealed record McpExecutionTokenResponse(
        string AccessToken,
        DateTimeOffset ExpiresAtUtc,
        string UserId,
        string Name,
        string? PrimaryOrganizationId,
        IReadOnlyList<string> Permissions,
        IReadOnlyList<string> AllowedOrganizationIds)
    { public bool SetupVerification { get; init; } }
}
