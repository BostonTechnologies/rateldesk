using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Helpdesk.API.Authentication;
using Helpdesk.API.Endpoints.Authentication;
using Helpdesk.Infrastructure.Identity;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.Services;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.API.Services.IntegrationCredentials;

/// <summary>Credential authority: ordinary grants stay unchanged; browser pairing also has a narrow setup-only path.</summary>
public sealed class IntegrationCredentialIssuer(
    IIntegrationCredentialOwnerResolver ownerResolver, ICurrentUserAccessService accessService,
    HelpdeskDbContext db, RatelDeskIdentityDbContext identityDb, TimeProvider? clock = null)
{
    public const string SetupVerificationPurpose = "mcp-setup";
    public static readonly TimeSpan SetupVerificationLifetime = TimeSpan.FromMinutes(5);
    public const int DefaultLifetimeDays = 30;
    public const int MaximumLifetimeDays = 90;

    public async Task<ValidatedCredentialGrant> ValidateAsync(ClaimsPrincipal principal,
        IntegrationCredentialEndpoints.CreateIntegrationCredentialRequest? request, CancellationToken ct)
    {
        var owner = await ownerResolver.ResolveAsync(principal, ct);
        if (owner is null) throw new CredentialGrantException(null);
        if (request is null) throw Invalid("request", "A credential request is required.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128)
            throw Invalid("name", "A credential name up to 128 characters is required.");
        var purpose = request.Purpose?.Trim().ToLowerInvariant();
        if (purpose is not ("api" or "mcp")) throw Invalid("purpose", "Purpose must be api or mcp.");
        var resource = purpose == "mcp" ? CanonicalMcpResourceUri(request.McpResourceUri) : null;
        if (purpose == "mcp" && resource is null)
            throw Invalid("mcpResourceUri", "MCP credentials require an absolute HTTPS resource URI ending in /mcp.");
        if (purpose == "api" && !string.IsNullOrWhiteSpace(request.McpResourceUri))
            throw Invalid("mcpResourceUri", "MCP resource URIs can only be configured for MCP credentials.");
        if (request.Permissions is null || request.Permissions.Count == 0 || request.Permissions.Any(string.IsNullOrWhiteSpace))
            throw Invalid("permissions", "At least one non-empty permission is required.");
        var permissions = request.Permissions.Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var ownerAdministration = request.GrantKind == IntegrationCredentialGrantKinds.OwnerAdministration;
        if (request.GrantKind is not (IntegrationCredentialGrantKinds.Ordinary or IntegrationCredentialGrantKinds.OwnerAdministration))
            throw Invalid("grantKind", "Choose ordinary or owner-administration access.");
        if (ownerAdministration && (purpose != "mcp" || !request.OwnerAdministrationConsent ||
            permissions.Length != 1 || permissions[0] != HelpdeskPermissions.HelpdeskAdmin))
            throw Invalid("ownerAdministrationConsent", "Owner administration requires an HTTP MCP credential, explicit instance-wide consent and the HelpdeskAdmin grant.");
        if (!ownerAdministration && permissions.Any(p => !HelpdeskPermissions.AssignablePermissions.Contains(p, StringComparer.OrdinalIgnoreCase)))
            throw Invalid("permissions", "Permissions must be known assignable permissions.");
        var organizationId = request.OrganizationId?.Trim();
        var organization = string.IsNullOrWhiteSpace(organizationId) ? null :
            await db.Organizations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == organizationId, ct);
        if (organization?.State != Helpdesk.Shared.Models.EntityState.Enabled)
            throw Invalid("organizationId", "An enabled organization is required.");
        var access = await accessService.ResolveAsync(principal, ct);
        if (ownerAdministration && (!access.IsHelpdeskAdmin || !await identityDb.Users.AsNoTracking()
            .AnyAsync(user => user.Id == owner.UserId && user.IsEnabled && user.IsInstanceAdministrator, ct)))
            throw new CredentialGrantException(null);
        if (!access.IsHelpdeskAdmin && permissions.Any(p => !access.HasPermission(p, organizationId)))
            throw new CredentialGrantException(null);
        if (request.LifetimeDays is < 1 or > MaximumLifetimeDays)
            throw Invalid("lifetimeDays", $"Lifetime must be between 1 and {MaximumLifetimeDays} days.");
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (request.ExpiresAtUtc is { } exactExpiry &&
            (exactExpiry.Offset != TimeSpan.Zero || exactExpiry <= now ||
             exactExpiry > now.AddDays(MaximumLifetimeDays) || exactExpiry.Ticks % 10 != 0))
            throw Invalid("expiresAtUtc", "Choose a future UTC expiry within 90 days, with at most six fractional second digits.");
        return new(owner.UserId, request.Name.Trim(), ownerAdministration ? IntegrationCredentialGrantKinds.OwnerAdministrationPurpose : purpose, organizationId!, permissions,
            request.LifetimeDays ?? DefaultLifetimeDays, resource) { ExpiresAtUtc = request.ExpiresAtUtc };
    }

    // Only the pairing service invokes this path, after identifying the persisted setup attempt.
    // Manual creation still uses ValidateAsync and cannot request this special grant.
    public async Task<ValidatedCredentialGrant> ValidateSetupVerificationAsync(ClaimsPrincipal principal,
        IntegrationCredentialEndpoints.CreateIntegrationCredentialRequest request, CancellationToken ct)
    {
        var owner = await ownerResolver.ResolveAsync(principal, ct);
        if (owner is null) throw new CredentialGrantException(null);
        if (request.Purpose != SetupVerificationPurpose || request.Permissions is null || request.Permissions.Count != 0 || request.LifetimeDays is not null || request.ExpiresAtUtc is not null)
            throw Invalid("permissions", "Setup verification grants only authenticated status, with no business permissions or configurable lifetime.");
        var resource = CanonicalMcpResourceUri(request.McpResourceUri);
        if (resource is null) throw Invalid("mcpResourceUri", "Setup verification requires the reviewed HTTPS /mcp resource.");
        var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == request.OrganizationId, ct);
        if (organization?.State != Helpdesk.Shared.Models.EntityState.Enabled)
            throw Invalid("organizationId", "An enabled organization is required.");
        var access = await accessService.ResolveAsync(principal, ct);
        if (!access.IsAuthenticated || (!access.IsHelpdeskAdmin && !access.AllowedOrganizationIds.Contains(organization.Id)))
            throw new CredentialGrantException(null);
        return new(owner.UserId, request.Name, SetupVerificationPurpose, organization.Id, [], 0, resource);
    }

    // Construction only: protected delivery must succeed before a pairing tracks this record.
    public (IntegrationCredential Record, IntegrationCredentialEndpoints.CreatedIntegrationCredential Delivery)
        Create(ValidatedCredentialGrant grant, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var record = new IntegrationCredential
        {
            Id = id, OwnerUserId = grant.OwnerUserId, Name = grant.Name, Prefix = $"rdk_{id:N}"[..16],
            SecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
            Purpose = grant.Purpose, McpResourceUri = grant.McpResourceUri, OrganizationId = grant.OrganizationId,
            Permissions = string.Join(' ', grant.Permissions.Order(StringComparer.OrdinalIgnoreCase)),
            CreatedAtUtc = now, CreatedAtUnixMilliseconds = now.ToUnixTimeMilliseconds(), ExpiresAtUtc = grant.Purpose == SetupVerificationPurpose ? now.Add(SetupVerificationLifetime) : grant.ExpiresAtUtc ?? now.AddDays(grant.LifetimeDays)
        };
        return (record, new(record.Id, record.Prefix, $"rdk_{id:N}_{secret}", record.Purpose is SetupVerificationPurpose or IntegrationCredentialGrantKinds.OwnerAdministrationPurpose ? "mcp" : record.Purpose,
            record.OrganizationId, grant.Permissions, record.ExpiresAtUtc)
        {
            McpResourceUri = record.McpResourceUri,
            GrantKind = grant.Purpose == SetupVerificationPurpose ? "setup-verification" :
                grant.Purpose == IntegrationCredentialGrantKinds.OwnerAdministrationPurpose ? IntegrationCredentialGrantKinds.OwnerAdministration : IntegrationCredentialGrantKinds.Ordinary
        });
    }

    public void Track(IntegrationCredential record) => identityDb.IntegrationCredentials.Add(record);

    public static string? CanonicalMcpResourceUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath.TrimEnd('/') != "/mcp") return null;
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static CredentialGrantException Invalid(string key, string message) => new(new() { [key] = [message] });
}

public sealed record ValidatedCredentialGrant(string OwnerUserId, string Name, string Purpose, string OrganizationId,
    IReadOnlyList<string> Permissions, int LifetimeDays, string? McpResourceUri)
{
    public DateTimeOffset? ExpiresAtUtc { get; init; }
}
public sealed class CredentialGrantException(Dictionary<string, string[]>? errors) : Exception("Credential grants were not accepted.")
{
    public Dictionary<string, string[]>? Errors { get; } = errors;
}

