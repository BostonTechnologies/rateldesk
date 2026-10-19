using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.API.Services.IntegrationCredentials;
using Helpdesk.Shared.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Helpdesk.API.Authentication;

public sealed class IntegrationCredentialAuthenticationOptions : AuthenticationSchemeOptions
{
    public string Purpose { get; set; } = IntegrationCredentialAuthenticationHandler.ApiPurpose;
}

public sealed class IntegrationCredentialAuthenticationHandler(
    IOptionsMonitor<IntegrationCredentialAuthenticationOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    RatelDeskIdentityDbContext identityDb)
    : AuthenticationHandler<IntegrationCredentialAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "IntegrationCredential";
    public const string McpSchemeName = "McpIntegrationCredential";
    public const string ApiPurpose = "api";
    public const string McpPurpose = "mcp";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer rdk_", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = authorization["Bearer ".Length..].Trim();
        var segments = token.Split('_', 3, StringSplitOptions.None);
        if (segments.Length != 3 || !Guid.TryParseExact(segments[1], "N", out var credentialId) || string.IsNullOrWhiteSpace(segments[2]))
            return AuthenticateResult.Fail("The integration credential is malformed.");

        var credential = await identityDb.IntegrationCredentials
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == credentialId, Context.RequestAborted)
            .ConfigureAwait(false);
        if (credential is null ||
            credential.RevokedAtUtc is not null ||
            credential.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            !(string.Equals(credential.Purpose, ExpectedPurpose, StringComparison.Ordinal) || (ExpectedPurpose == McpPurpose && credential.Purpose is IntegrationCredentialIssuer.SetupVerificationPurpose or IntegrationCredentialGrantKinds.OwnerAdministrationPurpose)))
        {
            return AuthenticateResult.Fail("The integration credential is not valid for this API.");
        }

        if (credential.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose &&
            (credential.Permissions.Length != 0 || string.IsNullOrWhiteSpace(credential.OrganizationId) ||
             credential.ExpiresAtUtc > credential.CreatedAtUtc.Add(IntegrationCredentialIssuer.SetupVerificationLifetime)))
            return AuthenticateResult.Fail("The setup verification grant is invalid.");

        var providedVerifier = SHA256.HashData(Encoding.UTF8.GetBytes(segments[2]));
        var storedVerifier = Convert.FromHexString(credential.SecretHash);
        if (!CryptographicOperations.FixedTimeEquals(providedVerifier, storedVerifier))
            return AuthenticateResult.Fail("The integration credential is not valid.");

        var owner = await identityDb.Users.AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == credential.OwnerUserId, Context.RequestAborted)
            .ConfigureAwait(false);
        if (owner?.IsEnabled != true)
            return AuthenticateResult.Fail("The integration credential owner is disabled.");
        if (credential.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose &&
            (!owner.IsInstanceAdministrator || credential.Permissions != HelpdeskPermissions.HelpdeskAdmin ||
             string.IsNullOrWhiteSpace(credential.OrganizationId)))
            return AuthenticateResult.Fail("The owner administration grant is no longer authorized.");

        // Do not retain a tracked authentication row across physical delete/revoke. The
        // conditional update also closes the read-to-last-used race without recreating access.
        var touched = await identityDb.IntegrationCredentials
            .Where(candidate => candidate.Id == credential.Id && candidate.RevokedAtUtc == null && candidate.SecretHash == credential.SecretHash)
            .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.LastUsedAtUtc, DateTimeOffset.UtcNow), Context.RequestAborted)
            .ConfigureAwait(false);
        if (touched != 1) return AuthenticateResult.Fail("The integration credential is no longer valid.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, owner.Id),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(owner.DisplayName) ? owner.UserName ?? owner.Email ?? owner.Id : owner.DisplayName),
            new(ClaimTypes.Email, owner.Email ?? string.Empty),
            new("auth_mode", "integration"),
            new("integration_credential_id", credential.Id.ToString("N")),
            new("integration_purpose", credential.Purpose)
        };
        if (credential.Purpose == IntegrationCredentialIssuer.SetupVerificationPurpose) claims.Add(new("setup_verification", "true"));
        if (!string.IsNullOrWhiteSpace(credential.OrganizationId))
            claims.Add(new Claim("integration_organization_id", credential.OrganizationId));
        if (!string.IsNullOrWhiteSpace(credential.McpResourceUri))
            claims.Add(new Claim("mcp_resource_uri", credential.McpResourceUri));
        foreach (var permission in credential.Permissions.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            claims.Add(new Claim("integration_permission", permission));

        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    private string ExpectedPurpose => Options.Purpose;
}
