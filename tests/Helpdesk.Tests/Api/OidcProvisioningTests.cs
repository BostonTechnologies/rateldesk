using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Helpdesk.API;
using Helpdesk.Infrastructure.Persistence;
using Helpdesk.Shared.Auth;
using Helpdesk.Shared.DTOs.Auth;
using Helpdesk.Shared.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Helpdesk.Tests.Api;

public sealed class OidcProvisioningTests
{
    [Theory]
    [InlineData("Oidc")]
    [InlineData("Hybrid")]
    public async Task Standard_subject_provisions_linked_user_and_preserves_persisted_tenant_roles(string mode)
    {
        using var fixture = new SignedOidcApi(mode);
        await fixture.SeedLinkedUserAsync();
        using var client = fixture.Client(fixture.Token(roles: ["TenantAdministrator"]));
        var response = await client.PostAsync("/api/v1/users/provision", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var access = await response.Content.ReadFromJsonAsync<CurrentUserAccessDto>();
        Assert.Equal("contact-a", access!.CustomerId);
        Assert.False(access.IsHelpdeskAdmin);
        Assert.Equal(["tenant-a"], access.AllowedOrganizationIds);
        Assert.DoesNotContain("tenant-b", access.ManagedOrganizationIds);
        Assert.Contains(access.ScopedPermissionGrants, grant => grant.OrganizationId == "tenant-a");
        Assert.All(access.ScopedPermissionGrants, grant => Assert.Equal("tenant-a", grant.OrganizationId));

        var current = await client.GetFromJsonAsync<CurrentUserAccessDto>("/api/v1/auth/me");
        Assert.Equal("contact-a", current!.CustomerId);
        Assert.False(current.IsHelpdeskAdmin);
        Assert.False(fixture.Factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("Authentik").MapInboundClaims);
    }

    [Theory]
    [InlineData("Oidc", "roles")]
    [InlineData("Hybrid", "groups")]
    public async Task Explicitly_mapped_external_admin_can_provision_without_a_customer_link(string mode, string roleClaim)
    {
        using var fixture = new SignedOidcApi(mode);
        using var client = fixture.Client(fixture.Token(roles: ["external-administrators"], roleClaim: roleClaim));
        var response = await client.PostAsync("/api/v1/users/provision", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<CurrentUserAccessDto>())!.IsHelpdeskAdmin);
    }

    [Theory]
    [InlineData("subject", "unlinked", HttpStatusCode.Forbidden)]
    [InlineData(null, "missing", HttpStatusCode.BadRequest)]
    [InlineData("other-subject", "same-email", HttpStatusCode.Forbidden)]
    public async Task Missing_or_unlinked_identity_cannot_be_repaired_with_email_or_request_body(string? subject, string scenario, HttpStatusCode expected)
    {
        using var fixture = new SignedOidcApi("Hybrid");
        await fixture.SeedLinkedUserAsync();
        using var client = fixture.Client(fixture.Token(subject: subject == "subject" ? "unlinked-subject" : subject));
        var response = await client.PostAsJsonAsync("/api/v1/users/provision", new { sub = "subject", iss = SignedOidcApi.Issuer, email = "linked@example.test", roles = new[] { "HelpdeskAdmin" } });
        Assert.Equal(expected, response.StatusCode);
        if (scenario == "missing")
            Assert.Contains("identity", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("expired")]
    public async Task Invalid_tokens_are_rejected_before_provisioning(string invalid)
    {
        using var fixture = new SignedOidcApi("Oidc");
        using var otherKey = RSA.Create(2048);
        var token = fixture.Token(
            issuer: invalid == "issuer" ? SignedOidcApi.Issuer + "wrong" : null,
            audience: invalid == "audience" ? "wrong-audience" : null,
            key: invalid == "signature" ? new RsaSecurityKey(otherKey) : null,
            expired: invalid == "expired");
        using var client = fixture.Client(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/users/provision", null)).StatusCode);
    }

    [Fact]
    public async Task Local_mode_rejects_external_bearer_tokens()
    {
        using var fixture = new SignedOidcApi("Local");
        using var client = fixture.Client(fixture.Token(roles: ["external-administrators"]));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v1/users/provision", null)).StatusCode);
    }
}

// Real production bearer registration; only provider discovery/signing material is synthetic.
internal sealed class SignedOidcApi : IDisposable
{
    public const string Issuer = "https://provider.example.test/application/o/rateldesk/";
    public const string Audience = "helpdesk-api";
    private readonly RSA _rsa = RSA.Create(2048);
    public RsaSecurityKey Key { get; }
    public WebApplicationFactory<Program> Factory { get; }

    public SignedOidcApi(string mode)
    {
        Key = new RsaSecurityKey(_rsa) { KeyId = "synthetic-provider-key" };
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseIsolatedTestStorage();
            builder.UseEnvironment("Development");
            builder.UseSetting("Authentication:Mode", mode);
            builder.UseSetting("Helpdesk:TestDatabaseName", $"oidc-provision-{Guid.NewGuid():N}");
            builder.UseSetting("Authentication:Authentik:Authority", Issuer);
            builder.UseSetting("Authentication:Authentik:Audience", Audience);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = mode,
                ["Authentication:Authentik:Authority"] = Issuer,
                ["Authentication:Authentik:Audience"] = Audience,
                ["Authentication:Authentik:RoleMappings:external-administrators"] = "HelpdeskAdmin"
            }));
            builder.ConfigureServices(services =>
            {
                foreach (var scheme in new[] { "Authentik", "Azure" })
                    services.PostConfigure<JwtBearerOptions>(scheme, options =>
                    {
                        var configuration = new OpenIdConnectConfiguration { Issuer = scheme == "Authentik" ? Issuer : "https://azure.example.test/" };
                        configuration.SigningKeys.Add(Key);
                        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                    });
            });
        });
    }

    public string Token(string? subject = "subject", string[]? roles = null, string roleClaim = "roles", string? issuer = null, string? audience = null, SecurityKey? key = null, bool expired = false, string? nonce = null)
    {
        var claims = new List<Claim>
        {
            new("email", "linked@example.test"), new("preferred_username", "Synthetic User"),
            new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        };
        if (subject is not null) claims.Add(new Claim("sub", subject));
        if (nonce is not null) claims.Add(new Claim("nonce", nonce));
        claims.AddRange((roles ?? []).Select(role => new Claim(roleClaim, role)));
        var token = new JwtSecurityToken(issuer ?? Issuer, audience ?? Audience, claims,
            DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(expired ? -5 : 5),
            new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public HttpClient Client(string token)
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task SeedLinkedUserAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HelpdeskDbContext>();
        db.Organizations.AddRange(new Organization { Id = "tenant-a", Name = "Tenant A" }, new Organization { Id = "tenant-b", Name = "Tenant B" });
        db.Customers.Add(new Customer { Id = "contact-a", Name = "Contact", Email = "linked@example.test", OrganizationId = "tenant-a" });
        db.Users.Add(new User { Id = "domain-a", Name = "Contact", Email = "linked@example.test", OrganizationId = "tenant-a", Role = "Customer" });
        db.CustomerAuthLinks.Add(new CustomerAuthLink { CustomerId = "contact-a", AuthProviderType = "Authentik", OidcIssuer = Issuer.TrimEnd('/'), OidcSubject = "subject", DomainUserId = "domain-a" });
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment { UserId = "domain-a", OrganizationId = "tenant-a", RoleKey = ScopedRoleCatalog.IncidentReader });
        await db.SaveChangesAsync();
    }

    public void Dispose() { Factory.Dispose(); _rsa.Dispose(); }
}
