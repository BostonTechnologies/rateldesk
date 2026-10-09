using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Helpdesk.Infrastructure.ServiceIdentity;

public sealed record IssuedServiceAccessToken(string AccessToken, int ExpiresIn, string Scope);

public sealed class ServiceAccessTokenService(ServiceSigningKeyStore keys, IServicePrincipalRegistry registry,
    IOptionsMonitor<ServiceIdentityOptions> options, TimeProvider time, IServicePublicSettingsResolver? publicSettings = null)
{
    public async Task<IssuedServiceAccessToken> IssueAsync(AuthenticatedServiceClient client, string[] scopes, CancellationToken ct = default)
    {
        if (scopes.Length == 0 || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length || scopes.Any(x => !registry.PermittedScopes(client.Principal, client.Credential).Contains(x, StringComparer.Ordinal))) throw new ArgumentException("invalid_scope");
        if (!await registry.CanIssueScopesAsync(client, scopes, ct)) throw new ArgumentException("invalid_scope");
        using var signing = await keys.GetSigningKeyAsync(ct);
        var settings = publicSettings is null ? options.CurrentValue : (await publicSettings.ResolveAsync(ct)).Identity;
        if (!settings.Enabled) throw new ServiceSigningKeyUnavailableException("Service connections are disabled.");
        var row = client.Principal;
        var claims = new List<Claim>
        {
            new("sub", $"service:{row.Id:N}"), new("client_id", row.ClientId), new("token_use", ServiceIdentityClaims.Purpose), new("auth_mode", "service"),
            new(ServiceIdentityClaims.PrincipalId, row.Id.ToString("N")), new(ServiceIdentityClaims.CredentialRevision, Number(client.Credential.CredentialRevision)),
            new(ServiceIdentityClaims.GrantRevision, Number(row.Revision)), new(ServiceIdentityClaims.OrganizationId, row.OrganizationId),
            new(ServiceIdentityClaims.PeerInstanceId, row.PeerInstanceId), new(ServiceIdentityClaims.PeerTenantId, row.PeerTenantId),
            new(ServiceIdentityClaims.MappingId, row.MappingId!.Value.ToString("D")), new(ServiceIdentityClaims.MappingRevision, Number(row.MappingRevision)), new("scope", string.Join(' ', scopes.Order(StringComparer.Ordinal))),
            new("jti", Guid.NewGuid().ToString("N")), new("target_instance_id", settings.InstanceId), new("caller_instance_id", row.PeerInstanceId),
            new("target_tenant_id", row.OrganizationId), new("caller_tenant_id", row.PeerTenantId)
        };
        var now = time.GetUtcNow();
        var expires = new[] { now.AddSeconds(settings.AccessTokenLifetimeSeconds), client.Credential.ExpiresAtUtc, client.Credential.RetireAtUtc ?? DateTimeOffset.MaxValue }.Min();
        var jwt = new JwtSecurityToken(settings.Issuer, settings.Audience, claims, now.UtcDateTime, expires.UtcDateTime, new SigningCredentials(signing.Key, SecurityAlgorithms.RsaSha256));
        jwt.Header["typ"] = "at+jwt";
        return new(new JwtSecurityTokenHandler().WriteToken(jwt), (int)(expires - now).TotalSeconds, string.Join(' ', scopes.Order(StringComparer.Ordinal)));
    }
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
