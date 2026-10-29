using System.Security.Claims;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Shared.Services;
using Helpdesk.Shared.Auth;
using Helpdesk.API.Endpoints.McpPolicies;
using Helpdesk.Infrastructure.McpPolicies;
using Helpdesk.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Authentication;

/// <summary>
/// Validates a gateway-delegated execution token. It always reloads the
/// source credential and account so revocation, expiry, account disablement,
/// and the current access projection take effect on the next API request.
/// </summary>
public sealed class McpExecutionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    McpExecutionTokenService executionTokens,
    RatelDeskIdentityDbContext identityDb,
    ICurrentUserAccessService accessService,
    HelpdeskDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "McpExecution";
    internal const string SetupOperationDeniedItem = "RatelDesk.SetupVerification.OperationDenied";
    private const string PolicyOperationDeniedItem = "RatelDesk.McpPolicy.OperationDenied";

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (!Context.Items.ContainsKey(PolicyOperationDeniedItem)) return base.HandleChallengeAsync(properties);
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Response.WriteAsJsonAsync(new { error = "mcp_policy_denied", message = "The current MCP policy does not allow this operation." }, Context.RequestAborted);
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = authorization["Bearer ".Length..].Trim();
        if (!executionTokens.TryRead(token, out var payload) || payload is null)
            return AuthenticateResult.Fail("The delegated MCP execution credential is invalid.");

        var credential = await identityDb.IntegrationCredentials
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == payload.CredentialId, Context.RequestAborted)
            .ConfigureAwait(false);
        if (credential is null || credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            credential.Purpose is not (IntegrationCredentialAuthenticationHandler.McpPurpose or IntegrationCredentialIssuer.SetupVerificationPurpose or IntegrationCredentialGrantKinds.OwnerAdministrationPurpose) ||
            !string.Equals(credential.OwnerUserId, payload.OwnerUserId, StringComparison.Ordinal) ||
            !string.Equals(credential.McpResourceUri, payload.ResourceUri, StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("The delegated MCP execution credential is no longer valid.");
        }

        if (!await IntegrationCredentialSetupAuthorization.IsUsableAsync(identityDb, credential.Id,
                DateTimeOffset.UtcNow, Context.RequestAborted).ConfigureAwait(false))
            return AuthenticateResult.Fail("The integration setup has not completed or its delivery expired.");

        if (credential.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose &&
            (credential.Permissions.Length != 0 || string.IsNullOrWhiteSpace(credential.OrganizationId) ||
             credential.ExpiresAtUtc > credential.CreatedAtUtc.Add(IntegrationCredentialIssuer.SetupVerificationLifetime)))
            return AuthenticateResult.Fail("The setup verification grant is invalid.");

        // The source row, not a client flag or a mapped None permission, determines the boundary.
        // Even a valid execution token cannot be replayed at another read-only/business endpoint.
        if (credential.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose &&
            (!HttpMethods.IsGet(Request.Method) || Request.Path != "/api/v1/auth/me" || Request.QueryString.HasValue))
        {
            Context.Items[SetupOperationDeniedItem] = true;
            return AuthenticateResult.Fail("Setup verification permits only authenticated status.");
        }

        var owner = await identityDb.Users.AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == credential.OwnerUserId, Context.RequestAborted)
            .ConfigureAwait(false);
        if (owner?.IsEnabled != true)
            return AuthenticateResult.Fail("The delegated MCP execution credential owner is disabled.");
        if (credential.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose &&
            (!owner.IsInstanceAdministrator || credential.Permissions != HelpdeskPermissions.HelpdeskAdmin ||
             string.IsNullOrWhiteSpace(credential.OrganizationId)))
            return AuthenticateResult.Fail("The owner administration grant is no longer authorized.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, owner.Id),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(owner.DisplayName) ? owner.UserName ?? owner.Email ?? owner.Id : owner.DisplayName),
            new(ClaimTypes.Email, owner.Email ?? string.Empty),
            new("auth_mode", "integration"),
            new("token_use", "mcp_execution"),
            new("integration_credential_id", credential.Id.ToString("N")),
            new("integration_purpose", credential.Purpose),
            new("mcp_resource_uri", credential.McpResourceUri ?? string.Empty)
        };
        if (credential.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose) claims.Add(new("setup_verification", "true"));
        if (!string.IsNullOrWhiteSpace(credential.OrganizationId))
            claims.Add(new Claim("integration_organization_id", credential.OrganizationId));
        foreach (var permission in credential.Permissions.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            claims.Add(new Claim("integration_permission", permission));

        var policy = await db.Set<McpAccessPolicy>().AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.CredentialId == credential.Id, Context.RequestAborted)
            .ConfigureAwait(false);
        if (policy is { DefinitionJson.Length: > 0 } && !McpPolicyDefinitionService.AllowsRequest(policy, credential,
                Request.Method, Request.Path.Value ?? "", (Context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
                DateTime.UtcNow, Request.Query))
        {
            Context.Items[PolicyOperationDeniedItem] = true;
            return AuthenticateResult.Fail("The current MCP policy does not allow this operation.");
        }

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        if (credential.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose)
        {
            var access = await accessService.ResolveAsync(principal, Context.RequestAborted).ConfigureAwait(false);
            if (!access.IsHelpdeskAdmin)
                return AuthenticateResult.Fail("The owner administration context is no longer authorized.");
        }
        else if (credential.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose)
        {
            // The organization may have been disabled or its owner's role removed since
            // exchange. Status must not turn that empty access projection into success.
            var access = await accessService.ResolveAsync(principal, Context.RequestAborted).ConfigureAwait(false);
            if (!access.IsAuthenticated || access.Permissions.Count != 0 || access.AllowedOrganizationIds.Count != 1 ||
                !access.AllowedOrganizationIds.Contains(credential.OrganizationId!))
                return AuthenticateResult.Fail("The setup verification organization is no longer authorized.");
        }
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}

